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

using System.ComponentModel;
using System.Globalization;
using NLog;
using WaveDudeNext.Engine;
using WaveDudeNext.Export;
using WaveDudeNext.Model;
using WaveDudeNext.Probes;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;
using WaveDudeNext.UI.Rendering;

namespace WaveDudeNext.UI;

/// <summary>An export failed, before or after it started: what the application tells the user, by the key of its text, with the error when there is one.</summary>
/// <param name="Key">The string table's key: "ExportError" or "RunError", or the text of what an MP4 needs that the computer lacks ("ExportMp4NeedsFfmpeg", "ExportMp4NeedsEncoder").</param>
/// <param name="Error">The error, or null when the message alone says it.</param>
public sealed record ExportFailure(string Key, Exception? Error);

/// <summary>
/// Carries out the exports (2026-10-14; a model of this library since 2026-11-07, the main window's command handlers
/// before): the Save dialog picks the format by its filter and the name, pre-set to the project's name with
/// <c>_field</c> / <c>_intensity</c> in the folder <see cref="ExportPaths"/> proposes; "both" writes two outputs from one
/// name; a still writes the frame the preview shows as one image file (<c>_field_0655</c>, the frame number padded as
/// a sequence names its files). Every export renders full-quality frames with the display settings as they are, the
/// outline when opted in and what the application draws over the frames (<see cref="IFrameDecorators"/>: the probe
/// graph boxes and markers, the source markers); a still shows exactly what a video export would show at that frame
/// (the user's rule), never the scratch frame.
/// <para>
/// <b>Preview runs and export runs</b> (2026-11-15, the user's design). The frames come from the preview's run where
/// its store holds what the export needs (fresh, complete, full resolution, every frame, 16-bit, the plane asked for;
/// one in progress is waited for, <see cref="IsWaiting"/>). Where it does not, the export computes them itself: an
/// <see cref="ExportRun"/> steps the simulation and hands the job each frame as it is rendered and written, and none
/// is stored. Until then a full-quality preview run was made first: its whole sequence had to fit the machine's
/// memory and cache whatever the View menu had been set to for that machine (10 GB for the default project, beyond
/// a browser page altogether), and it took the place of the run the preview showed. Now an export needs an engine,
/// one frame and the files it writes, and leaves the preview's run alone; the pictures are the same either way.
/// </para>
/// The <see cref="ExportJob"/> works on its own thread and reports to the busy indicator; Esc cancels it
/// (<see cref="Cancel"/>). Preview runs are not started or replaced while an export is in progress. A probe's
/// recording goes out as a CSV file the same way (2026-10-18): the dialog, the recording over the whole run (the
/// preview run's, or an export run's of every step and no frame), then the trace of the
/// probe written by <see cref="ProbeCsv"/> on the UI thread (a few megabytes at most). Export last (2026-10-28) is the
/// session's last run export again, no dialog and no question. A failure is raised as <see cref="Failed"/> for the
/// application to show; the harness hook <c>WAVEDUDE_EXPORT_PATH</c> stands in for the dialog. UI thread only.
/// </summary>
public sealed class Exporter : ObservableObject
{
	private static readonly ILogger Logger = LogManager.GetLogger(typeof(Exporter).FullName!);

	private readonly ProjectDocument _document;
	private readonly RunHost _runs;
	private readonly PreviewState _preview;
	private readonly BusyState _busy;
	private readonly SceneComposer _scene;
	private readonly ExportPaths _paths;
	private readonly IExportDialogs _dialogs;
	private readonly IFrameDecorators _decorators;
	private readonly SynchronizationContext _uiThread;
	private ExportJob? _job;
	private bool _isWaiting;
	/// <summary>Ends the run that computes a probe's recording for its CSV; null while none does.</summary>
	private CancellationTokenSource? _probeRun;

	/// <param name="document">The open project: what is exported, and the name its files take.</param>
	/// <param name="runs">The run exported, started first when the current one will not do.</param>
	/// <param name="preview">The position a still is taken at.</param>
	/// <param name="busy">Told the job and the wait for its run.</param>
	/// <param name="scene">The scene pixels the outline is computed from.</param>
	/// <param name="paths">Where the dialogs start and where the exports went.</param>
	/// <param name="dialogs">The Save dialog and the folder question.</param>
	/// <param name="decorators">What the application draws over the frames.</param>
	/// <param name="uiThread">The UI thread for the job's progress and end.</param>
	public Exporter(ProjectDocument document, RunHost runs, PreviewState preview, BusyState busy, SceneComposer scene, ExportPaths paths,
		IExportDialogs dialogs, IFrameDecorators decorators, SynchronizationContext uiThread)
	{
		_document = document;
		_runs = runs;
		_preview = preview;
		_busy = busy;
		_scene = scene;
		_paths = paths;
		_dialogs = dialogs;
		_decorators = decorators;
		_uiThread = uiThread;
	}

	/// <summary>An export failed: the application shows the text of the key, with the error's own words when there is one.</summary>
	public event EventHandler<ExportFailure>? Failed;

	/// <summary>The current job ended, completed, cancelled or failed: a run held back meanwhile may be due.</summary>
	public event EventHandler? Finished;

	/// <summary>The current export job (running or finished), or null.</summary>
	public ExportJob? Job
	{
		get => _job;
		private set
		{
			if (SetProperty(ref _job, value))
			{
				_busy.Export = value;
				OnPropertyChanged(nameof(IsWriting));
				OnPropertyChanged(nameof(IsExporting));
			}
		}
	}

	/// <summary>Whether an export is writing its frames.</summary>
	public bool IsWriting => _job?.IsActive == true;

	/// <summary>Whether an export is waiting for its run to complete before it starts writing.</summary>
	public bool IsWaiting
	{
		get => _isWaiting;
		private set
		{
			if (SetProperty(ref _isWaiting, value))
			{
				_busy.IsWaitingForRun = value;
				OnPropertyChanged(nameof(IsExporting));
			}
		}
	}

	/// <summary>Whether an export is running or waiting for its run.</summary>
	public bool IsExporting => IsWriting || _isWaiting;

	/// <summary>Whether an export can begin: none is in progress, and the project has steps to run.</summary>
	public bool CanExport => !IsExporting && Project.Simulation.Steps > 0;

	/// <summary>Whether Export last has something to repeat: a run export since the project was opened.</summary>
	public bool CanExportLast => _paths.LastRun != null && CanExport;

	/// <summary>Whether <paramref name="parameter"/> is a probe whose data can be exported: an active one, since a disabled one is in no run.</summary>
	public bool CanExportProbe(object? parameter) => parameter is Probe { Active: true } && CanExport;

	private Project Project => _document.Project;

	/// <summary>The Save dialog, the run, the targets, the job.</summary>
	/// <param name="field">Export the field.</param>
	/// <param name="intensity">Export the intensity.</param>
	/// <param name="still">One frame, the one the preview shows, as a single image file.</param>
	public async Task ExportAsync(bool field, bool intensity, bool still = false)
	{
		if (IsExporting)
		{
			return;
		}
		int stillFrame = Math.Max(0, _preview.Position);
		string suffix = field && intensity ? "" : field ? "_field" : "_intensity";
		if (still)
		{
			// the frame number, padded like a sequence's files, so a still of frame 655 is named as the sequence names it
			// (2026-10-19, the user's rule; the step number stood here until then)
			suffix += "_" + stillFrame.ToString("D" + ImageSequenceSink.MinDigits, CultureInfo.InvariantCulture);
		}
		string chosen;
		// the format chosen last is one of the full list; the still's filter lists the sequence formats alone, so a
		// remembered video means PNG there
		ExportFormat[] formats = still ? ExportFormats.Stills : ExportFormats.All;
		int formatIndex = Math.Max(0, Array.IndexOf(formats, _paths.Format));
		string? scripted = Environment.GetEnvironmentVariable("WAVEDUDE_EXPORT_PATH");
		if (!string.IsNullOrEmpty(scripted))
		{
			// a UI harness cannot drive the common dialog by posted keys: it hands the path in instead, "{name}" standing for the suggested name
			chosen = scripted.Replace("{name}", _document.Name + suffix, StringComparison.Ordinal);
			Logger.Debug("Export path taken from WAVEDUDE_EXPORT_PATH: {Path}", chosen);
		}
		else
		{
			// a still keeps the frame's name and takes the last still's folder; a run's export takes the last one's folder and name
			(string folder, string name) = still ? _paths.StartOfStill(_document.Name + suffix) : _paths.StartOfRun(_document.Name + suffix, suffix);
			ExportSubject subject = still ? ExportSubject.Still : field && intensity ? ExportSubject.Both : field ? ExportSubject.Field : ExportSubject.Intensity;
			if (await _dialogs.AskWhereToSaveAsync(subject, folder, name, formats, formatIndex) is not { } pick)
			{
				Logger.Debug("Export cancelled in the Save dialog");
				return;
			}
			chosen = pick.Path;
			formatIndex = Math.Clamp(pick.FormatIndex, 0, formats.Length - 1);
			if (!still)
			{
				_paths.Format = formats[formatIndex];
			}
			_paths.ChosenInDialog(chosen);
		}
		// the filter the user chose is the format, whatever extension was typed (the two MOV formats share theirs); a
		// scripted path has no filter and says its format by its extension, a .mov meaning the Animation codec
		ExportFormat format = formats[formatIndex];
		if (!string.IsNullOrEmpty(scripted))
		{
			format = ExportFormats.FromPath(chosen) ?? format;
		}
		if (still && !ExportFormats.IsSequence(format))
		{
			format = ExportFormat.Png;
		}
		if (!ExportFormats.Accepts(format, chosen))
		{
			chosen = Path.ChangeExtension(chosen, ExportFormats.Extension(format));
		}
		// remembered for the next dialog of the kind, in the settings too, and a run's for Export last
		if (still)
		{
			_paths.StillExported(chosen);
		}
		else
		{
			_paths.RunExported(field, intensity, format, chosen);
		}
		await StartAsync(field, intensity, still, stillFrame, format, chosen, ask: true);
	}

	/// <summary>Export last (Ctrl+E, 2026-10-28, the user's request): the session's last run export again, the same layers, format and path, no dialog and no question about what is there.</summary>
	public async Task ExportLastAsync()
	{
		if (IsExporting || _paths.LastRun is not { } last)
		{
			return;
		}
		Logger.Debug("Export last: the last export made again");
		await StartAsync(last.Field, last.Intensity, still: false, stillFrame: 0, last.Format, last.Path, ask: false);
	}

	/// <summary>
	/// The Save dialog, the probe's recording over the whole run, and that as CSV. The recording is the preview run's
	/// where that run serves; else a run of the export's own computes it, every step and no frame, on a thread of the
	/// pool, the busy indicator showing the wait and Esc ending it.
	/// </summary>
	public async Task ExportProbeAsync(Probe probe)
	{
		if (IsExporting)
		{
			return;
		}
		string suggested = ProbeCsv.FileName(_document.Name, probe);
		string chosen;
		string? scripted = Environment.GetEnvironmentVariable("WAVEDUDE_EXPORT_PATH");
		if (!string.IsNullOrEmpty(scripted))
		{
			// the harness hook of the picture exports: "{name}" stands for the suggested name
			chosen = scripted.Replace("{name}", Path.GetFileNameWithoutExtension(suggested), StringComparison.Ordinal);
			Logger.Debug("Export path taken from WAVEDUDE_EXPORT_PATH: {Path}", chosen);
		}
		else
		{
			(string folder, string name) = _paths.StartOfProbe(suggested);
			if (await _dialogs.AskWhereToSaveAsync(ExportSubject.ProbeData, folder, name, [], 0) is not { } pick)
			{
				Logger.Debug("Probe export cancelled in the Save dialog");
				return;
			}
			chosen = pick.Path;
			_paths.ChosenInDialog(chosen);
		}
		_paths.ProbeExported(chosen);
		Logger.Debug("Export: probe '{Probe}' ({Kind}, {Layer}, {Measurement}) as CSV to {Path}", probe.Name, probe.Kind, probe.Layer, probe.Measurement, chosen);
		bool intensity = probe.Layer == ProbeLayer.Intensity;
		SimulationRun? stored = await ServingRunAsync(intensity, needsHalves: false, intensityRoot: null);
		RunSpec spec;
		IReadOnlyList<ProbeTrace> traces;
		ExportRun? own = null;
		if (stored != null)
		{
			spec = stored.Spec;
			traces = stored.Traces;
		}
		else
		{
			// a run of the export's own, for the probes alone: every step, no frame captured or stored
			IsWaiting = true;
			_probeRun = new CancellationTokenSource();
			try
			{
				spec = RunSpec.Create(Project, _scene.Load(), Settings.Default.UseGpu, trackIntensity: intensity, PreviewQuality.Full);
				own = new ExportRun(spec);
				ExportRun run = own;
				CancellationToken cancel = _probeRun.Token;
				await Task.Run(() => run.RunToEnd(cancel));
				traces = own.Traces;
			}
			catch (OperationCanceledException)
			{
				Logger.Warn("Probe export cancelled while its run was computed; nothing was written");
				own?.Dispose();
				return;
			}
			catch (Exception ex)
			{
				// whatever keeps the run from being made or its engine from stepping: shown as a run's error is
				Logger.Error(ex, "The export's run failed: {Message}", ex.Message);
				own?.Dispose();
				Failed?.Invoke(this, new ExportFailure("RunError", ex));
				return;
			}
			finally
			{
				_probeRun.Dispose();
				_probeRun = null;
				IsWaiting = false;
			}
		}
		try
		{
			ProbeTrace? trace = null;
			for (int i = 0; i < spec.Probes.Length && i < traces.Count; i++)
			{
				if (ReferenceEquals(spec.Probes[i].Origin, probe))
				{
					trace = traces[i];
				}
			}
			if (trace == null)
			{
				Logger.Error("Probe '{Probe}' is not in the run (removed or disabled while it was waited for)", probe.Name);
				Failed?.Invoke(this, new ExportFailure("ExportError", null));
				return;
			}
			int rows = ProbeCsv.Write(chosen, probe, trace, spec.VisualizeEvery, spec.FrameStride);
			Logger.Info("Probe data written: {Rows} rows of '{Probe}' to {Path}", rows, probe.Name, _dialogs.Shown(chosen));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.Error(ex, "The probe data could not be written: {Message}", ex.Message);
			Failed?.Invoke(this, new ExportFailure("ExportError", ex));
		}
		finally
		{
			own?.Dispose();
		}
	}

	/// <summary>Cancels the job in progress, if any (its outputs hold what was written), and a run that computes a probe's recording for its CSV.</summary>
	public void Cancel()
	{
		_job?.Cancel();
		_probeRun?.Cancel();
	}

	/// <summary>Cancels a running export and waits briefly for its files to close; for closing the application.</summary>
	public void Shutdown()
	{
		_probeRun?.Cancel();
		ExportJob? job = _job;
		if (job?.IsActive != true)
		{
			return;
		}
		job.Cancel();
		if (!job.Completion.Wait(TimeSpan.FromSeconds(10)))
		{
			Logger.Warn("The export did not stop in time; its last file may be incomplete");
		}
	}

	/// <summary>
	/// The frames (the preview run's, or a run of the export's own), the targets, the job: the pictures <paramref name="field"/>
	/// and <paramref name="intensity"/> as <paramref name="format"/> at <paramref name="chosen"/>, whose extension is the
	/// format's; a <paramref name="still"/> is the one frame <paramref name="stillFrame"/> as a single image file.
	/// </summary>
	/// <param name="field">Export the field.</param>
	/// <param name="intensity">Export the intensity.</param>
	/// <param name="still">One frame as a single image file.</param>
	/// <param name="stillFrame">The frame of a still.</param>
	/// <param name="format">The format, chosen in the dialog or by the scripted path's extension.</param>
	/// <param name="chosen">The file, its extension the format's; for both layers the name their two files are made from.</param>
	/// <param name="ask">Whether a sequence folder that exists with files in it is replaced only on request; Export last replaces it without asking.</param>
	private async Task StartAsync(bool field, bool intensity, bool still, int stillFrame, ExportFormat format, string chosen, bool ask)
	{
		Logger.Debug("Export: {What} as {Format} to {Path}", still ? "the shown frame " + stillFrame : field && intensity ? "field and intensity" : field ? "field" : "intensity", format, chosen);
		if (ExportFormats.ObstacleTo(format) is { } obstacle)
		{
			// what the platform's H.264 encoder needs is not there: the log says what, and where installing something
			// is the remedy the message's own text says it too
			(string reason, string key) = obstacle switch
			{
				ExportObstacle.FfmpegMissing => ("no ffmpeg was found on the path; installing ffmpeg is the remedy", "ExportMp4NeedsFfmpeg"),
				ExportObstacle.FfmpegWithoutH264 => ("the ffmpeg on the path has no H.264 encoder (libx264 or libopenh264)", "ExportMp4NeedsEncoder"),
				ExportObstacle.BrowserWithoutH264 => ("this browser hands a page no H.264 encoder", "ExportError"),
				_ => ("Media Foundation is missing on this computer (Windows N needs the Media Feature Pack)", "ExportError"),
			};
			Logger.Error("MP4 export is not available: {Reason}", reason);
			Failed?.Invoke(this, new ExportFailure(key, null));
			return;
		}
		// the output paths: one per layer; "both" takes the chosen name without a layer suffix and appends its own
		var outputs = new List<(FrameLayer Layer, string Path)>();
		if (field && intensity)
		{
			string folder = Path.GetDirectoryName(chosen) ?? "";
			string name = Path.GetFileNameWithoutExtension(chosen);
			foreach (string known in ExportPaths.LayerSuffixes)
			{
				if (name.EndsWith(known, StringComparison.OrdinalIgnoreCase))
				{
					name = name[..^known.Length];
				}
			}
			string extension = ExportFormats.Extension(format);
			outputs.Add((FrameLayer.Field, Path.Combine(folder, name + "_field" + extension)));
			outputs.Add((FrameLayer.Intensity, Path.Combine(folder, name + "_intensity" + extension)));
		}
		else
		{
			outputs.Add((field ? FrameLayer.Field : FrameLayer.Intensity, chosen));
		}
		// a sequence goes to a folder of its own; one that exists with files in it is replaced only on request (Export last asks nothing)
		if (!still && ExportFormats.IsSequence(format))
		{
			foreach ((_, string path) in outputs)
			{
				string folder = ExportFormats.SequenceFolder(path);
				if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any())
				{
					continue;
				}
				if (!ask)
				{
					Logger.Debug("Export last: {Folder} replaced without asking", folder);
				}
				else if (!await _dialogs.AskToReplaceFolderAsync(folder))
				{
					Logger.Debug("Export cancelled: {Folder} kept", folder);
					return;
				}
			}
		}
		// the frames: the preview's run where its stored frames serve as they are; else a run of the export's own, which
		// computes each frame as it is written and stores none (2026-11-15, the user's design; until then a full-quality
		// preview run was made first, whose frames the machine had to hold, and which took the preview's run's place)
		bool? root = intensity ? ExportFormats.IntensityRootFor(format) : null;
		SimulationRun? stored = await ServingRunAsync(intensity, needsHalves: true, root);
		ExportRun? own = null;
		RunSpec spec;
		IFrameSource frames;
		IReadOnlyList<ProbeTrace> traces;
		if (stored != null)
		{
			spec = stored.Spec;
			frames = stored.Store;
			traces = stored.Traces;
		}
		else
		{
			try
			{
				spec = RunSpec.Create(Project, _scene.Load(), Settings.Default.UseGpu, trackIntensity: intensity, PreviewQuality.Full with { IntensityRoot = root ?? true });
			}
			catch (Exception ex) when (ex is RunSpecException or IOException or UnauthorizedAccessException or NotSupportedException)
			{
				Logger.Error(ex, "The export's run could not start: {Message}", ex.Message);
				Failed?.Invoke(this, new ExportFailure("RunError", ex));
				return;
			}
			own = new ExportRun(spec);
			frames = own;
			traces = own.Traces;
		}
		// the range in frames, from the model's steps like the preview's in/out points; a still is the shown frame
		Display display = Project.Display;
		int every = spec.VisualizeEvery;
		int lastFrame = spec.FrameCount - 1;
		int first;
		int last;
		if (still)
		{
			first = last = Math.Clamp(stillFrame, 0, lastFrame);
		}
		else
		{
			first = Math.Clamp(display.ExportFrom / every, 0, lastFrame);
			last = display.ExportTo <= 0 ? lastFrame : Math.Clamp(display.ExportTo / every, first, lastFrame);
		}
		int frameCount = last - first + 1;
		Logger.Debug("Export range: frames {First} to {Last} ({Count}) of the run's {Total}, {Width}×{Height}, {Source}", first, last, frameCount, spec.FrameCount, spec.Width, spec.Height,
			stored != null ? "from the preview run's store " + stored.Store.Path : "computed by a run of the export's own, none stored");
		// the targets: the layer's colours and outline, what the application draws over the frames, the sink
		var targets = new List<ExportTarget>();
		try
		{
			byte[]? edges = null;
			if (display.Field.OverlayInExport || display.Intensity.OverlayInExport)
			{
				ScenePixels? scene = _scene.Load();
				if (scene != null && scene.Width == spec.Width && scene.Height == spec.Height)
				{
					edges = GlowingEdges.Compute(scene.Bgra, scene.Width, scene.Height, scene.StrideBytes);
				}
				Logger.Debug("Export outline: {State}", edges != null ? "computed from the scene image" : "none (no image scene)");
			}
			IFrameDecorator decorator = _decorators.Create(Project, spec, traces);
			foreach ((FrameLayer layer, string path) in outputs)
			{
				DisplayLayer settings = layer == FrameLayer.Field ? display.Field : display.Intensity;
				ColorLut lut = ColorSchemeCatalog.Instance.LutFor(Project, settings.ColorScheme, settings.BlackLevel);
				var exportLayer = new ExportLayer(layer, lut, (float)settings.Brightness, settings.OverlayInExport ? edges : null, settings.OverlayGain, decorator.For(layer));
				// the sink is opened by the job on its thread (a Media Foundation writer must stay on one thread)
				int width = spec.Width;
				int height = spec.Height;
				int fps = display.Video.Fps;
				int quality = display.Video.Quality;
				string output = !still && ExportFormats.IsSequence(format) ? ExportFormats.SequenceFolder(path) : path;
				Func<IFrameSink> open = still
					? () => ExportFormats.OpenStill(format, path, width, height, quality)
					: () => ExportFormats.Open(format, path, width, height, fps, quality, frameCount, first);
				targets.Add(new ExportTarget(exportLayer, output, open));
				if (ExportFormats.IsData(format))
				{
					Logger.Debug("Export target {Layer}: the stored 16-bit values as OpenEXR{Samples} (no colours, outline or boxes), to {Output}",
						layer, layer == FrameLayer.Intensity ? (frames.IntensityRoot ? ", the intensity's square root" : ", the intensity itself") : "", output);
				}
				else
				{
					Logger.Debug("Export target {Layer}: scheme '{Scheme}' (black level {Black}), brightness {Brightness}, outline {Outline}, probe boxes {Boxes}, markers {Markers}, sources {Sources}, to {Output}",
						layer, settings.ColorScheme, settings.BlackLevel, settings.Brightness, settings.OverlayInExport && edges != null ? "gain " + settings.OverlayGain : "off", display.ProbeGraphsInExport ? "on" : "off", display.ProbeMarkersInExport ? "on" : "off", display.SourceMarkersInExport ? "on" : "off", output);
				}
			}
			if (!still && format == ExportFormat.Mpeg1)
			{
				double used = Mpeg1Writer.FrameRates[Mpeg1Writer.NearestFrameRateCode(display.Video.Fps) - 1];
				if (Math.Abs(used - display.Video.Fps) > 0.01)
				{
					Logger.Warn("MPEG-1 has no {Wanted} fps: the video is written at {Used:0.###} fps", display.Video.Fps, used);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or RunSpecException)
		{
			Logger.Error(ex, "The export could not start: {Message}", ex.Message);
			own?.Dispose();
			Failed?.Invoke(this, new ExportFailure("ExportError", ex));
			return;
		}
		var job = new ExportJob(frames, first, last, targets, _uiThread);
		job.PropertyChanged += OnJobPropertyChanged;
		job.Finished += (_, _) =>
		{
			// the job's threads have ended: the export's own run lets its engine go
			own?.Dispose();
			OnJobFinished(job);
		};
		Job = job;
		Logger.Info("Export started: frames {First} to {Last} ({Count}) of {Width}×{Height} to {Outputs}{Run}",
			first, last, frameCount, spec.Width, spec.Height, string.Join(", ", targets.Select(target => _dialogs.Shown(target.Output))), own != null ? ", each frame computed as it is written" : "");
		job.Start();
	}

	/// <summary>
	/// The preview's run when its stored frames and recordings can serve the export as they are: fresh, complete (one
	/// in progress is waited for), of full quality, and holding the plane asked for. Null when it cannot, which the log
	/// says with the reason: the export then computes what it needs by a run of its own (<see cref="ExportRun"/>),
	/// and the preview's run stays what it is. Until 2026-11-15 a full-quality preview run was started here instead,
	/// with its frames in the store, whatever the View menu and the machine's memory said.
	/// </summary>
	/// <param name="intensity">The run must hold the intensity plane.</param>
	/// <param name="needsHalves">The run must hold 16-bit samples (every picture or data export); the probe data does with the 8-bit index.</param>
	/// <param name="intensityRoot">What the run's 16-bit intensity must hold for an OpenEXR export of it: its square root (true), the intensity (false), or either (null).</param>
	private async Task<SimulationRun?> ServingRunAsync(bool intensity, bool needsHalves, bool? intensityRoot)
	{
		for (int attempt = 0; attempt < 3; attempt++)
		{
			SimulationRun? run = _runs.Run;
			// a reduced preview never serves a picture or data export: a smaller picture or skipped frames are not the
			// run, and the 8-bit index has only the palette's 255 colours where the 16-bit samples give every level of the
			// gradient (2026-10-23, the user's choice; from 2026-10-18 a picture export had taken an 8-bit run as it was)
			PreviewQuality? quality = run?.Spec.Quality;
			bool reduced = quality != null && (quality.ResolutionDivisor != 1 || quality.FrameDivisor != 1 || (quality.Format != FrameFormat.Half && needsHalves));
			string? reason = run == null ? "there is no preview run" : _runs.IsRunStale ? "the preview run is stale" : _runs.IsProbesStale ? "the probes were edited since the preview run"
				: reduced ? (quality!.Format != FrameFormat.Half && needsHalves && quality.ResolutionDivisor == 1 && quality.FrameDivisor == 1 ? "the export needs the 16-bit samples" : "the preview run is a reduced preview (View menu)")
				: intensity && !run.Store.Has(FrameLayer.Intensity) ? "the preview run has no intensity plane"
				: intensityRoot is bool root && run.Store.IntensityRoot != root ? (root ? "the export needs the intensity as its square root" : "the export needs the intensity as I, which the preview stores as its square root")
				: !run.IsActive && run.State != RunState.Completed ? "the preview run did not complete" : null;
			if (reason != null)
			{
				Logger.Debug("Export: computed by a run of the export's own ({Reason})", reason);
				return null;
			}
			if (run!.State == RunState.Completed)
			{
				Logger.Debug("Export: from the preview run's stored frames");
				return run;
			}
			Logger.Debug("Export: waiting for the preview run in progress, whose frames serve");
			IsWaiting = true;
			try
			{
				await run.Completion;
				await WaitForState(run);
			}
			finally
			{
				IsWaiting = false;
			}
			// looked at again: completed, replaced meanwhile (an edit started another), or ended otherwise
		}
		Logger.Debug("Export: computed by a run of the export's own (the preview run kept changing while it was waited for)");
		return null;
	}

	/// <summary>The run's final state is posted to the UI thread after its completion task completes; wait for that post.</summary>
	private static async Task WaitForState(SimulationRun run)
	{
		for (int i = 0; i < 50 && !run.IsFinished; i++)
		{
			await Task.Delay(20);
		}
	}

	private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ExportJob.State) && ReferenceEquals(sender, _job))
		{
			OnPropertyChanged(nameof(IsWriting));
			OnPropertyChanged(nameof(IsExporting));
		}
	}

	private void OnJobFinished(ExportJob job)
	{
		if (!ReferenceEquals(job, _job))
		{
			return;
		}
		string outputs = string.Join(", ", job.Targets.Select(target => _dialogs.Shown(target.Output)));
		switch (job.State)
		{
			case ExportState.Completed:
				Logger.Info("Exported {Frames} frames to {Outputs} in {Seconds:0.0} s", job.FramesDone, outputs, job.Elapsed.TotalSeconds);
				break;
			case ExportState.Cancelled:
				Logger.Warn("Export cancelled after {Frames} of {Total} frames; {Outputs} hold what was written", job.FramesDone, job.FrameCount, outputs);
				break;
			default:
				Logger.Error("Export failed after {Frames} frames: {Message}", job.FramesDone, job.Error?.Message);
				if (job.Error != null)
				{
					Failed?.Invoke(this, new ExportFailure("ExportError", job.Error));
				}
				break;
		}
		Finished?.Invoke(this, EventArgs.Empty);
	}
}
