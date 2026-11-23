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
using System.Runtime.InteropServices.JavaScript;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Probes;
using static WaveDudeNext.Browser.ScriptThread;

namespace WaveDudeNext.Browser;

/// <summary>
/// The browser's GPU engine (2026-11-13, the user's choice of WebGPU): <see cref="IWaveEngine"/> on the graphics card
/// through WebGPU compute shaders, the OpenCL engine's kernels in WGSL (<c>webgpu-engine.js</c>, which this class
/// drives) on the tables the library's <see cref="GpuTables"/> makes for both. Same algorithm and layout as
/// <see cref="WaveSimulator"/> and <see cref="GpuWaveSimulator"/>: the field, the scene's maps, the sources and the
/// probes live on the card, a capture is converted there and read back.
/// <para>
/// Threads: one thread drives an engine, as the contract says, and it is not the thread the page's scripts are called
/// from, which is the runtime's main thread. Every call to the card is posted there and waited for
/// (<see cref="ScriptThread"/>), so the driving thread blocks as it does in OpenCL's blocking calls. That costs a hand-over
/// between threads, so the steps are not sent one by one: <see cref="AdvanceOneStep"/> and <see cref="SampleProbes"/>
/// only note what was asked, in order, and the notes go to the card in one call when something needs the result (a
/// capture, a read, a change of the scene) or <see cref="FlushAt"/> of them have gathered. The card's completion is
/// not waited for at such a call: in the browser that answer takes about 3 ms to arrive whatever the work (measured
/// 2026-11-13), and a read or an awaited capture waits for everything sent before it anyway.
/// </para>
/// <para>
/// Not the OpenCL engine's: the samples of a capture are not bit for bit the CPU's (WebGPU promises no correctly
/// rounded square root, and the sources' sine is single precision on the card). Measured through this contract
/// against the CPU engine (2026-11-13, three scenes, both stencils, 200 steps): the field agrees to about 1e-5 of its
/// maximum, one or two in a hundred of a capture's halves differ by one step of a half, and one palette index in three
/// thousand, in one capture of eight, by 1.
/// </para>
/// </summary>
public sealed partial class WebGpuWaveSimulator : IWaveEngine, IDisposable
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(WebGpuWaveSimulator).FullName!);

	/// <summary>Noted steps and probe samplings after which they are sent to the card without waiting for a reason.</summary>
	private const int FlushAt = 64;

	private static string _adapter = "";
	private static bool _described;

	/// <summary>The graphics adapter the engines run on, as the browser names it; empty until the first engine is made, and where there is none. Any thread.</summary>
	public static string Adapter => _adapter;

	private readonly int _width;
	private readonly int _height;
	private readonly int _rim;
	private readonly int _canvasWidth;
	private readonly int _canvasHeight;
	private readonly int _stride;
	private readonly int _cells;
	private readonly bool _openEdges;
	private readonly bool _track;
	private readonly bool _fourthOrder;
	private readonly int _sourceCount;
	private readonly double _timeDecay;
	private readonly double _sourceFrequencyScale;
	private readonly float _intensityScale;
	private readonly bool[] _levelUsed = new bool[256];
	private readonly float[] _hostField;
	private readonly float[]? _hostIntensity;
	private readonly float[]? _hostStage1;
	private readonly float[]? _hostStage2;
	private readonly List<byte> _operations = [];
	private readonly Dictionary<FrameSnapshot, int> _captures = [];
	private readonly int _dTableCapacity;
	private readonly int _engine;
	private List<Emitter> _objSources = [];
	private ProbeSampler? _probeSampler;
	private List<float>[] _probeValues = [];
	private int[] _profileStart = [];
	private int _profileCount;
	private int _subSteps;
	private bool _disposed;

	/// <summary>
	/// The engine for <paramref name="scene"/>, or null with the reason: no WebGPU in this browser, no adapter, or a
	/// scene larger than one storage binding of the device. The signature is <see cref="WaveEngineFactory.PlatformGpu"/>'s.
	/// </summary>
	public static IWaveEngine? TryCreate(SceneData scene, EngineOptions options, IReadOnlyList<Emitter>? emitters, out string problems)
	{
		problems = "";
		try
		{
			string description = Await(async () =>
			{
				// beside the page, wherever that is served from: the address counts from the runtime's folder
				await JSHost.ImportAsync("webgpu-engine", "../webgpu-engine.js");
				SetHistoryRows(GpuTables.ProbeRows);
				return await Init(Emitter.DetachRampSteps, GpuTables.EdgeSpeedFactor);
			});
			string[] parts = description.Split('|');
			_adapter = parts[0];
			if (!_described)
			{
				// a device's facts once per process (the log must let a report be diagnosed)
				_described = true;
				Logger.Debug("WebGPU device: {Facts}", parts.Length > 1 ? parts[1] : parts[0]);
			}
			return new WebGpuWaveSimulator(scene, options, emitters);
		}
		catch (Exception ex)
		{
			problems = ex.Message;
			return null;
		}
	}

	private WebGpuWaveSimulator(SceneData scene, EngineOptions options, IReadOnlyList<Emitter>? emitters)
	{
		Description = "GPU: " + _adapter + " (WebGPU)";
		_width = scene.Width;
		_height = scene.Height;
		_rim = scene.Rim;
		_canvasWidth = scene.CanvasWidth;
		_canvasHeight = scene.CanvasHeight;
		_openEdges = scene.OpenEdges;
		_stride = _width + 2;
		_cells = _stride * (_height + 2);
		_track = options.TrackIntensity;
		_fourthOrder = options.FourthOrderLaplacian;
		_sourceCount = scene.SourceCount;
		_timeDecay = options.TimeDecay;
		_sourceFrequencyScale = options.SourceFrequencyScale;
		_intensityScale = (float)options.IntensityScale;
		if (_cells > Invoke(MaxCells))
		{
			throw new InvalidOperationException("the scene is too large for one WebGPU storage buffer of this device");
		}
		_hostField = new float[_cells];
		_hostIntensity = _track ? new float[_cells] : null;
		_hostStage1 = _track ? new float[_cells] : null;
		_hostStage2 = _track ? new float[_cells] : null;
		GpuTables.SceneTables maps = GpuTables.Scene(scene, _fourthOrder);
		_subSteps = maps.SubSteps;
		_dTableCapacity = Math.Max(GpuTables.DTableRoom, maps.DTable.Length);
		float[] dTable = new float[_dTableCapacity];
		maps.DTable.CopyTo(dTable, 0);
		float[] c2Table = new float[256];
		maps.C2Table.CopyTo(c2Table, 0);
		float[] frequencies = GpuTables.Frequencies(_sourceFrequencyScale);
		byte[] imageSources = ImageSourceBytes(scene);
		float alpha = GpuTables.IntensityAlpha(_timeDecay, _levelUsed, []);
		float sourceAmplitude = (float)options.SourceAmplitude;
		_engine = Invoke(() => Create(_width, _height, _stride, _rim, _canvasWidth, _canvasHeight, _fourthOrder, _track, _openEdges, _subSteps, maps.C2Mult, alpha,
			_intensityScale, sourceAmplitude, _sourceCount, _dTableCapacity, maps.C2Index, MemoryMarshal.AsBytes(maps.DIndex.AsSpan()), MemoryMarshal.AsBytes(c2Table.AsSpan()),
			MemoryMarshal.AsBytes(dTable.AsSpan()), MemoryMarshal.AsBytes(frequencies.AsSpan()), imageSources));
		// what a report needs to say: the grid the kernels run over, and how (as the other engines log it)
		Logger.Debug("WebGPU kernels built on {Device}: grid {Width} × {Height} (canvas {CanvasWidth} × {CanvasHeight}, rim {Rim}, {Edges}), stride {Stride}, work groups 32 × 8, {Stencil}",
			_adapter, _width, _height, _canvasWidth, _canvasHeight, _rim, _openEdges ? "open" : "reflective", _stride, GpuTables.StencilText(_fourthOrder, _subSteps));
		SetEmitters(emitters);
	}

	/// <inheritdoc/>
	public string Description { get; }

	/// <inheritdoc/>
	public int Width => _canvasWidth;

	/// <inheritdoc/>
	public int Height => _canvasHeight;

	/// <inheritdoc/>
	public int Stride => _stride;

	/// <inheritdoc/>
	public int Rim => _rim;

	/// <summary>Host copy of the field (padded layout) as of the last <see cref="PrepareFrame"/> with <c>needField</c>.</summary>
	public float[] FieldData => _hostField;

	/// <summary>Host copy of the scaled intensity (padded layout) as of the last <see cref="PrepareFrame"/> with <c>needIntensity</c>; null when the intensity is not tracked.</summary>
	public float[]? IntensityData => _hostIntensity;

	/// <summary>The image sources as the kernel's pairs (padded cell, level), noting the levels in use.</summary>
	private byte[] ImageSourceBytes(SceneData scene)
	{
		(int[] indices, int[] levels) = GpuTables.ImageSources(scene, _levelUsed);
		int[] pairs = new int[indices.Length * 2];
		for (int i = 0; i < indices.Length; i++)
		{
			pairs[i * 2] = indices[i];
			pairs[i * 2 + 1] = levels[i];
		}
		return MemoryMarshal.AsBytes(pairs.AsSpan()).ToArray();
	}

	/// <inheritdoc/>
	public void SetEmitters(IReadOnlyList<Emitter>? emitters)
	{
		Flush();
		List<Emitter> inside = GpuTables.InCanvas(emitters, _canvasWidth, _canvasHeight);
		_objSources = inside;
		// the order the card applies them in: emitters that share a cell go into passes of their own, launched one after
		// the other, since those of one launch run at once and each reads and writes its cell (GpuTables.Passes, 2026-11-19)
		(List<Emitter> ordered, int[] passEnds) = GpuTables.Passes(inside);
		GpuTables.EmitterTables t = GpuTables.Emitters(ordered, _rim, _stride, _sourceFrequencyScale);
		// sixteen 32-bit words an element, the kernel's structure
		byte[] table = new byte[ordered.Count * 64];
		Span<int> words = MemoryMarshal.Cast<byte, int>(table.AsSpan());
		for (int i = 0; i < ordered.Count; i++)
		{
			Span<int> e = words.Slice(i * 16, 16);
			e[0] = t.Index[i];
			e[1] = BitConverter.SingleToInt32Bits(t.Omega[i]);
			e[2] = BitConverter.SingleToInt32Bits(t.Amplitude[i]);
			e[3] = BitConverter.SingleToInt32Bits(t.Phase[i]);
			e[4] = t.Mode[i];
			e[5] = t.PulseShape[i];
			e[6] = t.OnWidth[i];
			e[7] = t.OffWidth[i];
			e[8] = t.DetachStep[i];
			e[9] = t.OffWidthBase[i];
			e[10] = t.RandomizeOffWidthAmount[i];
			e[11] = BitConverter.SingleToInt32Bits(t.CycleAnchor[i]);
			e[12] = t.RngState[i];
		}
		// the intensity's averaging time constant follows the lowest source level in use (image or object)
		float alpha = GpuTables.IntensityAlpha(_timeDecay, _levelUsed, inside);
		byte[] ends = MemoryMarshal.AsBytes(passEnds.AsSpan()).ToArray();
		Invoke(() => SetEmitters(_engine, ordered.Count, table, alpha, ends));
	}

	/// <inheritdoc/>
	public void Reset()
	{
		_operations.Clear();
		lock (_captures)
		{
			_captures.Clear();
		}
		Invoke(() => Reset(_engine));
		// the emitters' cycle state lives on the card: their table is made again from the seeds
		SetEmitters(_objSources);
		ForgetProbes();
	}

	/// <inheritdoc/>
	public bool TryReplaceScene(SceneData scene)
	{
		if (scene.Width != _width || scene.Height != _height || scene.Rim != _rim || scene.OpenEdges != _openEdges || scene.SourceCount != _sourceCount)
		{
			return false;
		}
		GpuTables.SceneTables maps = GpuTables.Scene(scene, _fourthOrder);
		if (maps.DTable.Length > _dTableCapacity)
		{
			// more distinct damping values than the table buffer holds: a rebuild sizes it
			return false;
		}
		Flush();
		_subSteps = maps.SubSteps;
		float[] c2Table = new float[256];
		maps.C2Table.CopyTo(c2Table, 0);
		byte[] imageSources = ImageSourceBytes(scene);
		Invoke(() => SetScene(_engine, maps.SubSteps, maps.C2Mult, maps.C2Index, MemoryMarshal.AsBytes(maps.DIndex.AsSpan()), MemoryMarshal.AsBytes(c2Table.AsSpan()),
			MemoryMarshal.AsBytes(maps.DTable.AsSpan()), imageSources));
		// Reset re-derives the intensity constant from the levels now in use, through SetEmitters
		Reset();
		Logger.Debug("Scene replaced on {Device}: {Stencil}", _adapter, GpuTables.StencilText(_fourthOrder, _subSteps));
		return true;
	}

	/// <summary>Notes one step; it runs on the card with the next <see cref="Flush"/>.</summary>
	public void AdvanceOneStep() => Note(0);

	/// <summary>Notes the probes' sampling of the state after the last step; it runs on the card with the next <see cref="Flush"/>.</summary>
	public void SampleProbes()
	{
		if (_probeSampler != null)
		{
			Note(1);
		}
	}

	private void Note(byte operation)
	{
		_operations.Add(operation);
		if (_operations.Count >= FlushAt)
		{
			Flush();
		}
	}

	/// <summary>
	/// Sends the noted steps and samplings to the card, in their order. The card is not waited for, except when many
	/// steps have gone out with nothing awaited since (the script decides): what reads from the card afterwards waits
	/// for everything before it.
	/// </summary>
	private void Flush()
	{
		if (_operations.Count == 0)
		{
			return;
		}
		byte[] operations = TakeOperations();
		Await(() => Run(_engine, operations));
	}

	/// <summary>The noted steps and samplings, taken out for the call that sends them.</summary>
	private byte[] TakeOperations()
	{
		byte[] operations = [.. _operations];
		_operations.Clear();
		return operations;
	}

	/// <inheritdoc/>
	public void PrepareFrame(bool needField, bool needIntensity)
	{
		Flush();
		if (needField)
		{
			ReadFloats(0, _hostField);
		}
		if (needIntensity && _hostIntensity != null)
		{
			// the blend of the two stages, the CPU engine's expression, multiply and add apart
			ReadFloats(1, _hostStage1!);
			ReadFloats(2, _hostStage2!);
			float[] s1 = _hostStage1!, s2 = _hostStage2!, blend = _hostIntensity;
			float scale = _intensityScale;
			for (int i = 0; i < blend.Length; i++)
			{
				blend[i] = (s1[i] * 0.5f + s2[i] * 0.5f) * scale;
			}
		}
	}

	/// <summary>The card's floats of a buffer (0 the newest field, 1 and 2 the intensity's stages) into <paramref name="target"/>.</summary>
	private void ReadFloats(int what, float[] target)
	{
		GCHandle pin = GCHandle.Alloc(target, GCHandleType.Pinned);
		try
		{
			nint address = pin.AddrOfPinnedObject();
			int bytes = target.Length * 4;
			Await(() => Read(_engine, what, address, bytes));
		}
		finally
		{
			pin.Free();
		}
	}

	/// <summary>An ordinary snapshot: the card's planes are copied into the program's memory when a capture is awaited.</summary>
	public FrameSnapshot CreateSnapshot(FrameShape shape, bool withIntensity) => new(shape.Validate(), withIntensity);

	/// <inheritdoc/>
	public void CaptureFrame(FrameSnapshot snapshot, bool needIntensity)
	{
		FrameShape shape = snapshot.Shape;
		if (shape.CanvasWidth != _canvasWidth || shape.CanvasHeight != _canvasHeight)
		{
			throw new ArgumentException("The snapshot's canvas does not match the engine's size.", nameof(snapshot));
		}
		lock (_captures)
		{
			if (_captures.ContainsKey(snapshot))
			{
				throw new InvalidOperationException("The snapshot has a capture in flight; await it first.");
			}
		}
		bool withIntensity = needIntensity && _track && snapshot.HasIntensityPlane;
		snapshot.HasIntensity = withIntensity;
		// the noted steps go with the capture, in one hand-over, and the card is not waited for: the read is begun
		// there and awaited in AwaitFrame, on the writer's thread in a run
		byte[] operations = TakeOperations();
		int ticket = Await(() => Capture(_engine, operations, shape.Format == FrameFormat.Index, shape.Divisor, shape.Width, shape.Height, withIntensity, shape.IntensityRoot,
			shape.BrightnessOf(false), shape.BrightnessOf(true)));
		lock (_captures)
		{
			_captures[snapshot] = ticket;
		}
	}

	/// <inheritdoc/>
	public unsafe void AwaitFrame(FrameSnapshot snapshot)
	{
		int ticket;
		lock (_captures)
		{
			if (!_captures.Remove(snapshot, out ticket))
			{
				return;
			}
		}
		Span<byte> intensityPlane = snapshot.HasIntensity ? snapshot.IntensityBytes : default;
		fixed (byte* field = snapshot.FieldBytes)
		fixed (byte* intensity = intensityPlane)
		{
			nint fieldAddress = (nint)field, intensityAddress = (nint)intensity;
			Await(() => AwaitCapture(_engine, ticket, fieldAddress, intensityAddress));
		}
	}

	/// <inheritdoc/>
	public void SetProbes(ProbeSampler? sampler)
	{
		Flush();
		ForgetProbes();
		if (sampler == null || sampler.Count == 0)
		{
			return;
		}
		if (sampler.NeedsIntensity && !_track)
		{
			throw new InvalidOperationException("the intensity is not tracked");
		}
		int count = sampler.Count;
		GpuTables.SeriesProbeTables series = sampler.HasSeries ? GpuTables.SeriesProbes(sampler) : new GpuTables.SeriesProbeTables([], []);
		GpuTables.ProfileProbeTables profiles = sampler.HasProfiles ? GpuTables.ProfileProbes(sampler) : new GpuTables.ProfileProbeTables([], [], [], [], [], false);
		// a line pixel as the kernel takes it: cell, mode, start flag, start step
		int[] profilePixels = new int[profiles.Pixels.Length * 4];
		for (int k = 0; k < profiles.Pixels.Length; k++)
		{
			profilePixels[k * 4] = profiles.Pixels[k];
			profilePixels[k * 4 + 1] = profiles.Modes[k];
			profilePixels[k * 4 + 2] = profiles.StartFlags[k];
			profilePixels[k * 4 + 3] = profiles.StartSteps[k];
		}
		try
		{
			Invoke(() => SetProbes(_engine, count, sampler.HasSeries ? series.Pixels.Length : 0, MemoryMarshal.AsBytes(series.Pixels.AsSpan()), MemoryMarshal.AsBytes(series.Info.AsSpan()),
				profiles.Pixels.Length, MemoryMarshal.AsBytes(profilePixels.AsSpan()), profiles.Integrates));
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException("the probes could not be set up on the GPU: " + ex.Message, ex);
		}
		_profileStart = profiles.ProfileStart;
		_profileCount = profiles.Pixels.Length;
		_probeValues = new List<float>[count];
		for (int i = 0; i < count; i++)
		{
			_probeValues[i] = [];
		}
		_probeSampler = sampler;
	}

	/// <summary>The probes go, here and on the card.</summary>
	private void ForgetProbes()
	{
		if (_probeSampler != null)
		{
			Invoke(() => SetProbes(_engine, 0, 0, default, default, 0, default, false));
		}
		_probeSampler = null;
		_probeValues = [];
		_profileCount = 0;
	}

	/// <inheritdoc/>
	public float[][] TakeProbeSamples()
	{
		if (_probeSampler is not { } sampler)
		{
			return [];
		}
		int count = sampler.Count;
		if (sampler.HasSeries)
		{
			Flush();
			int rows = Await(() => ProbeRows(_engine));
			if (rows > 0)
			{
				float[] history = new float[rows * count];
				GCHandle pin = GCHandle.Alloc(history, GCHandleType.Pinned);
				try
				{
					nint address = pin.AddrOfPinnedObject();
					Invoke(() => TakeProbeRows(_engine, address));
				}
				finally
				{
					pin.Free();
				}
				for (int row = 0; row < rows; row++)
				{
					for (int probe = 0; probe < count; probe++)
					{
						if (!sampler.IsProfile[probe])
						{
							_probeValues[probe].Add(history[row * count + probe]);
						}
					}
				}
			}
		}
		float[][] samples = new float[count][];
		for (int i = 0; i < count; i++)
		{
			samples[i] = sampler.IsProfile[i] ? [] : [.. _probeValues[i]];
			_probeValues[i].Clear();
		}
		return samples;
	}

	/// <inheritdoc/>
	public float[]?[] ReadProbeProfiles()
	{
		if (_probeSampler is not { } sampler)
		{
			return [];
		}
		int count = sampler.Count;
		float[]?[] profiles = new float[count][];
		if (!sampler.HasProfiles)
		{
			return profiles;
		}
		Flush();
		float[] values = new float[_profileCount];
		GCHandle pin = GCHandle.Alloc(values, GCHandleType.Pinned);
		try
		{
			nint address = pin.AddrOfPinnedObject();
			Await(() => ReadProfiles(_engine, address));
		}
		finally
		{
			pin.Free();
		}
		for (int i = 0; i < count; i++)
		{
			if (sampler.IsProfile[i])
			{
				int length = sampler.Pixels[i].Length;
				float[] profile = new float[length];
				Array.Copy(values, _profileStart[i], profile, 0, length);
				profiles[i] = profile;
			}
		}
		return profiles;
	}

	/// <summary>Releases the engine's buffers on the card. Safe to call more than once.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_operations.Clear();
		try
		{
			Invoke(() => Destroy(_engine));
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "The WebGPU engine's buffers could not be released");
		}
	}

	[JSImport("init", "webgpu-engine")]
	private static partial Task<string> Init(int detachRampSteps, float edgeSpeedFactor);

	[JSImport("setHistoryRows", "webgpu-engine")]
	private static partial void SetHistoryRows(int rows);

	[JSImport("maxCells", "webgpu-engine")]
	private static partial int MaxCells();

	[JSImport("create", "webgpu-engine")]
	private static partial int Create(int width, int height, int stride, int rim, int canvasWidth, int canvasHeight, bool fourthOrder, bool track, bool openEdges, int subSteps,
		float c2Scale, float alpha, float intensityScale, float sourceAmplitude, int sourceCount, int dTableCapacity,
		[JSMarshalAs<JSType.MemoryView>] Span<byte> c2Index, [JSMarshalAs<JSType.MemoryView>] Span<byte> dIndex, [JSMarshalAs<JSType.MemoryView>] Span<byte> c2Table,
		[JSMarshalAs<JSType.MemoryView>] Span<byte> dTable, [JSMarshalAs<JSType.MemoryView>] Span<byte> frequencies, [JSMarshalAs<JSType.MemoryView>] Span<byte> imageSources);

	[JSImport("setScene", "webgpu-engine")]
	private static partial void SetScene(int engine, int subSteps, float c2Scale, [JSMarshalAs<JSType.MemoryView>] Span<byte> c2Index, [JSMarshalAs<JSType.MemoryView>] Span<byte> dIndex,
		[JSMarshalAs<JSType.MemoryView>] Span<byte> c2Table, [JSMarshalAs<JSType.MemoryView>] Span<byte> dTable, [JSMarshalAs<JSType.MemoryView>] Span<byte> imageSources);

	[JSImport("setEmitters", "webgpu-engine")]
	private static partial void SetEmitters(int engine, int count, [JSMarshalAs<JSType.MemoryView>] Span<byte> table, float alpha, [JSMarshalAs<JSType.MemoryView>] Span<byte> passEnds);

	[JSImport("reset", "webgpu-engine")]
	private static partial void Reset(int engine);

	[JSImport("run", "webgpu-engine")]
	private static partial Task Run(int engine, [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] operations);

	[JSImport("read", "webgpu-engine")]
	private static partial Task Read(int engine, int what, nint address, int bytes);

	[JSImport("capture", "webgpu-engine")]
	private static partial Task<int> Capture(int engine, [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] operations, bool index, int divisor, int width, int height, bool withIntensity,
		bool root, float fieldBrightness, float intensityBrightness);

	[JSImport("awaitCapture", "webgpu-engine")]
	private static partial Task AwaitCapture(int engine, int ticket, nint fieldAddress, nint intensityAddress);

	[JSImport("setProbes", "webgpu-engine")]
	private static partial void SetProbes(int engine, int count, int seriesPixels, [JSMarshalAs<JSType.MemoryView>] Span<byte> pixels, [JSMarshalAs<JSType.MemoryView>] Span<byte> info,
		int profileCount, [JSMarshalAs<JSType.MemoryView>] Span<byte> profilePixels, bool integrates);

	[JSImport("probeRows", "webgpu-engine")]
	private static partial Task<int> ProbeRows(int engine);

	[JSImport("takeProbeRows", "webgpu-engine")]
	private static partial void TakeProbeRows(int engine, nint address);

	[JSImport("readProfiles", "webgpu-engine")]
	private static partial Task ReadProfiles(int engine, nint address);

	[JSImport("destroy", "webgpu-engine")]
	private static partial void Destroy(int engine);
}
