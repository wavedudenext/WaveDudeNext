// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alexander Graef <alex@graef.cc>
//
// This file is part of WaveDude Next, a 2D wave simulator.
//
// Based on the work of
//   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
//     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
//   0x23 (Diffraction Limited), author of WaveSimulator2D
//     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Runs;

/// <summary>
/// A run for an export (2026-11-15, the user's design: preview runs and export runs): it steps an engine through a
/// <see cref="RunSpec"/> and hands each frame out as it is asked for, the frames in rising order, and keeps none of
/// them. A preview run (<see cref="SimulationRun"/>) stores its whole sequence in a <see cref="FrameStore"/> so the
/// preview can play and scrub it; an export needs each frame once, on its way into a file, and until this class it
/// got them by a full-quality preview run made first, whose store a machine may not have the memory for (Full HD,
/// 2500 steps, both planes: 10.4 GB), least of all a browser page, and which replaced the run the preview showed.
/// The export job reads this as it reads a store (<see cref="IFrameSource"/>), so what it renders and writes is
/// the same either way: frame k is the state after step k × <see cref="RunSpec.VisualizeEvery"/>, the probes are
/// sampled every step and collected every frame, as a preview run does it.
/// <para>
/// Threads: one thread drives it from the first frame asked for to the last (the export's render thread), and
/// that thread alone reads <see cref="Traces"/>, which hold each probe's recording up to the frame last handed out;
/// the engine is made at the first frame, on that thread. <see cref="Dispose"/> is called when that thread is done.
/// What it keeps: the engine, one snapshot, the probes' recordings.
/// </para>
/// </summary>
public sealed class ExportRun : IFrameSource, IDisposable
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(ExportRun).FullName!);

	private IWaveEngine? _engine;
	private ProbeSampler? _sampler;
	private ProbeCollector? _collector;
	private FrameSnapshot? _snapshot;
	/// <summary>Steps done so far.</summary>
	private int _steps;
	/// <summary>The frame the snapshot holds; -1 before the first.</summary>
	private int _frame = -1;
	private bool _disposed;

	/// <summary>Prepares the run; the engine is made when the first frame is asked for.</summary>
	/// <param name="spec">What to run; its shape is the frames', so an export hands in one of full quality.</param>
	public ExportRun(RunSpec spec)
	{
		Spec = spec;
		Traces = new ProbeTrace[spec.Probes.Length];
		for (int i = 0; i < Traces.Length; i++)
		{
			Traces[i] = new ProbeTrace();
		}
	}

	/// <summary>What is run.</summary>
	public RunSpec Spec { get; }

	/// <summary>
	/// Every probe's recording up to the frame last handed out, one per <see cref="RunSpec.Probes"/>: the series up to
	/// that frame's step, a line probe's profiles up to that frame. Read on the driving thread alone.
	/// </summary>
	public ProbeTrace[] Traces { get; }

	/// <summary>The engine in use ("CPU (16 threads)", "GPU: … (OpenCL)"); empty until the first frame was asked for.</summary>
	public string EngineDescription { get; private set; } = "";

	/// <summary>Steps done so far.</summary>
	public int StepsDone => _steps;

	/// <inheritdoc/>
	public FrameShape Shape => Spec.Shape;

	/// <summary>The frames of the whole run: each can be asked for once its predecessors have been.</summary>
	public int Count => Spec.FrameCount;

	/// <inheritdoc/>
	public bool Has(FrameLayer layer) => layer == FrameLayer.Field || Spec.TrackIntensity;

	/// <inheritdoc/>
	/// <exception cref="InvalidOperationException">A frame before the one last handed out was asked for.</exception>
	public bool CopyLayer(int frame, FrameLayer layer, Span<byte> destination)
	{
		if (_disposed || (layer != FrameLayer.Field && !Spec.TrackIntensity))
		{
			return false;
		}
		if (frame != _frame)
		{
			AdvanceTo(frame);
		}
		(layer == FrameLayer.Field ? _snapshot!.FieldBytes : _snapshot!.IntensityBytes).CopyTo(destination);
		return true;
	}

	/// <inheritdoc/>
	public bool CopyLayer(int frame, FrameLayer layer, Span<Half> destination)
	{
		if (Shape.Format != FrameFormat.Half)
		{
			throw new InvalidOperationException("The export run's samples are palette indices, not halves.");
		}
		return CopyLayer(frame, layer, MemoryMarshal.AsBytes(destination));
	}

	/// <summary>
	/// Runs every step that is left without taking a frame, for an export of the probes' recordings alone (a
	/// probe's CSV): <see cref="Traces"/> then hold the whole run.
	/// </summary>
	/// <param name="cancel">Ends the run between two steps.</param>
	/// <exception cref="OperationCanceledException">Cancelled.</exception>
	public void RunToEnd(CancellationToken cancel)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		EnsureEngine();
		Step(Spec.Steps, cancel);
		// the steps after the last frame's: their samples are still with the engine
		_collector?.Collect(_engine!, _steps);
		ApplyBatch();
		Logger.Debug("Export run for the probes: {Steps} steps on {Engine}, no frame taken", _steps, EngineDescription);
	}

	/// <summary>Steps on to <paramref name="frame"/> and captures it.</summary>
	private void AdvanceTo(int frame)
	{
		if (frame < _frame)
		{
			throw new InvalidOperationException($"An export run hands its frames out in order: frame {frame} was asked for after frame {_frame}.");
		}
		if (frame >= Count)
		{
			throw new InvalidOperationException($"Frame {frame} is beyond the run, which has {Count}.");
		}
		EnsureEngine();
		// frame k is the state after the step with the 0-based index k × every, as a preview run captures it
		Step(frame * Spec.VisualizeEvery + 1, CancellationToken.None);
		ApplyBatch();
		_snapshot!.Frame = frame;
		_snapshot.Step = frame * Spec.VisualizeEvery;
		_engine!.CaptureFrame(_snapshot, Spec.TrackIntensity);
		_engine.AwaitFrame(_snapshot);
		_frame = frame;
	}

	/// <summary>Steps until <paramref name="stepsDone"/> steps are done: every step samples the probes, every frame's step collects them, as <see cref="SimulationRun"/> does.</summary>
	private void Step(int stepsDone, CancellationToken cancel)
	{
		IWaveEngine engine = _engine!;
		int every = Spec.VisualizeEvery;
		while (_steps < stepsDone && _steps < Spec.Steps)
		{
			cancel.ThrowIfCancellationRequested();
			engine.AdvanceOneStep();
			if (_sampler != null)
			{
				engine.SampleProbes();
			}
			int index = _steps++;
			if (index % every == 0)
			{
				_collector?.Collect(engine, _steps);
				_collector?.CollectProfiles(engine, _steps);
			}
		}
	}

	/// <summary>What the collector gathered since the last frame goes into <see cref="Traces"/>, as a preview run's batches go into its own.</summary>
	private void ApplyBatch()
	{
		if (_collector == null)
		{
			return;
		}
		ProbeUpdate update = _collector.TakeForUi();
		for (int i = 0; i < Traces.Length && i < update.Samples.Length; i++)
		{
			Traces[i].Series.Add(update.Samples[i]);
			foreach (float[] profile in update.Profiles[i])
			{
				Traces[i].AddProfile(profile, update.Lengths[i]);
			}
			Traces[i].StepsSampled = update.StepsDone;
		}
	}

	/// <summary>The scene, the probes' sampler and the engine, at the first use, on the driving thread.</summary>
	private void EnsureEngine()
	{
		if (_engine != null)
		{
			return;
		}
		SceneData scene = Spec.BuildScene();
		_sampler = Spec.Probes.Length > 0 ? ProbeSampler.Build(Spec.Probes, scene.CanvasWidth, scene.CanvasHeight, scene.Rim, Spec.Seed) : null;
		_collector = _sampler != null ? new ProbeCollector(_sampler) : null;
		_engine = WaveEngineFactory.Create(scene, Spec.Engine, Spec.PreferGpu, Spec.Emitters, out string note);
		EngineDescription = _engine.Description;
		Logger.Info("Export run: {Steps} steps, {Frames} frames of {Width}×{Height} computed as they are written, none stored; engine: {Engine}",
			Spec.Steps, Spec.FrameCount, Spec.Shape.Width, Spec.Shape.Height, EngineDescription);
		if (note.Length > 0)
		{
			Logger.Warn(note);
		}
		_engine.SetProbes(_sampler);
		// the engine's own snapshot: page-locked on the GPU, released with the engine
		_snapshot = _engine.CreateSnapshot(Spec.Shape, Spec.TrackIntensity);
	}

	/// <summary>Releases the engine; a frame asked for afterwards is not there.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			(_engine as IDisposable)?.Dispose();
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "The export run's engine could not be released cleanly.");
		}
		_engine = null;
		_snapshot = null;
	}
}
