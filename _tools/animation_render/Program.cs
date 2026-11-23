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

using System.Diagnostics;
using System.Globalization;
using NLog;
using NLog.Config;
using NLog.Targets;
using WaveDudeNext.Export;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;
using WaveDudeNext.UI;
using WaveDudeNext.UI.Rendering;
using WaveDudeNext.WPF.Platform;

namespace AnimationRender;

/// <summary>What the command line asks for.</summary>
/// <param name="Project">The project file.</param>
/// <param name="Out">The folder the two animations go to.</param>
/// <param name="Name">The stem of their names; <c>_field.webp</c> and <c>_intensity.webp</c> follow it.</param>
/// <param name="LastFrame">The last frame of the animation, as the timeline numbers it; 0 for the project's whole export range.</param>
/// <param name="Width">Width of the animations in pixels.</param>
/// <param name="Fps">Frames per second of the animations.</param>
/// <param name="Speed">Playback speed against the project's video frame rate.</param>
/// <param name="Quality">Quality of the lossy WebP frames, 1 to 100.</param>
/// <param name="Ffmpeg">The ffmpeg executable.</param>
/// <param name="AllowDiskCache">Whether frames that do not fit the free memory may go to the frame cache on disk.</param>
/// <param name="Verbose">Whether the library's log is shown from Info up rather than from Warn.</param>
internal sealed record Options(string Project, string Out, string Name, int LastFrame, int Width, int Fps, double Speed, int Quality, string Ffmpeg, bool AllowDiskCache, bool Verbose);

/// <summary>
/// Renders a project's run as two animated WebP pictures, the field and the intensity, without a window: what
/// File > Export > Export both writes, made by the same pieces (the scene with the parts painted in, a full-quality
/// run, the layers' colour schemes, brightness and outline, the WPF head's probe graph boxes and markers), with
/// each rendered frame piped into ffmpeg (<see cref="FfmpegSink"/>) instead of a video file. The run is only as long
/// as the animation: with a last frame given, the steps beyond it are not computed, so a graph whose step axis spans
/// the whole run spans the animation.
/// </summary>
internal static class Program
{
	private const string Usage = """
		AnimationRender <project.wdproj> --out <folder> [--name <stem>] [--last-frame <n>] [--width <px>] [--fps <n>]
		                [--speed <factor>] [--quality <1..100>] [--ffmpeg <path>] [--allow-disk-cache] [--verbose]

		Writes <stem>_field.webp and <stem>_intensity.webp into the folder: the project's run, field and intensity, as
		looping animated WebP pictures. --last-frame ends the animation at that frame of the timeline (0, the default:
		the project's whole export range); --width (480) and --fps (15) are the animation's; --speed (1) plays it faster
		than the project's video frame rate, in fewer frames. The frames are kept in memory: a run that does not fit is
		refused, unless --allow-disk-cache lets the rest go to the frame cache on disk.
		Exit codes: 0 done, 1 failed, 2 the command line, 3 the frames do not fit the free memory.
		""";

	[STAThread]
	private static int Main(string[] args)
	{
		Options? options = Parse(args, out string? complaint);
		if (options == null)
		{
			Console.Error.WriteLine(complaint == null ? Usage : complaint + Environment.NewLine + Environment.NewLine + Usage);
			return 2;
		}
		// the library's own log on the error stream: warnings and worse, or everything from Info with --verbose
		var config = new LoggingConfiguration();
		config.AddRule(options.Verbose ? LogLevel.Info : LogLevel.Warn, LogLevel.Fatal,
			new ConsoleTarget("console") { StdErr = true, Layout = "    ${level:uppercase=true} ${logger:shortName=true}: ${message}${onexception:inner= ${exception:format=message}}" });
		LogManager.Configuration = config;
		try
		{
			return Render(options);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or RunSpecException or InvalidOperationException or FormatException)
		{
			Console.Error.WriteLine($"{options.Name}: {ex.Message}");
			return 1;
		}
		finally
		{
			LogManager.Shutdown();
		}
	}

	/// <summary>Reads the command line; null with a complaint for one that cannot be read, null without for none at all.</summary>
	private static Options? Parse(string[] args, out string? complaint)
	{
		complaint = null;
		string? project = null;
		string? folder = null;
		string? name = null;
		int lastFrame = 0;
		int width = 480;
		int fps = 15;
		double speed = 1;
		int quality = 75;
		string ffmpeg = "ffmpeg";
		bool allowDiskCache = false;
		bool verbose = false;
		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			if (!arg.StartsWith("--", StringComparison.Ordinal))
			{
				if (project != null)
				{
					complaint = $"One project at a time: '{project}' and '{arg}'.";
					return null;
				}
				project = arg;
				continue;
			}
			if (arg == "--allow-disk-cache")
			{
				allowDiskCache = true;
				continue;
			}
			if (arg == "--verbose")
			{
				verbose = true;
				continue;
			}
			if (i + 1 >= args.Length)
			{
				complaint = $"'{arg}' needs a value.";
				return null;
			}
			string value = args[++i];
			bool understood = arg switch
			{
				"--out" => Take(value, out folder),
				"--name" => Take(value, out name),
				"--ffmpeg" => Take(value, out ffmpeg!),
				"--last-frame" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out lastFrame),
				"--width" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out width) && width >= 16,
				"--fps" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out fps) && fps >= 1,
				"--speed" => double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out speed) && speed > 0,
				"--quality" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out quality) && quality is >= 1 and <= 100,
				_ => false,
			};
			if (!understood)
			{
				complaint = $"'{arg} {value}' is not understood.";
				return null;
			}
		}
		if (project == null || folder == null)
		{
			complaint = args.Length == 0 ? null : "A project file and --out <folder> are needed.";
			return null;
		}
		return new Options(Path.GetFullPath(project), Path.GetFullPath(folder), name ?? Path.GetFileNameWithoutExtension(project), lastFrame, width, fps, speed, quality, ffmpeg, allowDiskCache, verbose);
	}

	private static bool Take(string value, out string? taken)
	{
		taken = value;
		return value.Length > 0;
	}

	/// <summary>The run and the export of <paramref name="options"/>; the process's exit code.</summary>
	private static int Render(Options options)
	{
		Project project = Project.Load(options.Project);
		// a run no longer than the animation: frame k is the state after step k × VisualizeEvery
		Simulation simulation = project.Simulation;
		if (options.LastFrame > 0)
		{
			simulation.Steps = Math.Min(simulation.Steps, (options.LastFrame + 1) * Math.Max(1, simulation.VisualizeEvery));
		}
		// the scene as the app composes it: the embedded image (or its file beside the project) with the enabled parts
		// painted in, or the parts on a blank canvas; null for a canvas without parts
		Scene scene = project.Scene;
		ScenePixels? image = null;
		if (scene.Mode == SceneMode.Image)
		{
			if (scene.ImageData == null && scene.Image.Length == 0)
			{
				throw new RunSpecException("The scene uses an image, but no image file is set.");
			}
			byte[] data = scene.ImageData ?? File.ReadAllBytes(Path.GetFullPath(scene.Image, Path.GetDirectoryName(options.Project)!));
			image = new WpfImageDecoder().Decode(data);
		}
		ScenePixels? pixels = PartPainter.Paint(image, scene.Width, scene.Height, scene.RefractiveIndex, project.Parts);

		// what every export of the app is made from: 16-bit samples, full size, every frame, both planes
		RunSpec spec = RunSpec.Create(project, pixels, true, true, PreviewQuality.Full);
		long budget = FrameMemory.Budget(FrameMemory.Automatic);
		int ramFrames = Math.Min(spec.StoredFrameCount, FrameStore.FramesInBudget(spec.Shape, spec.Layers, budget));
		if (ramFrames < spec.StoredFrameCount)
		{
			double needed = FrameMemory.ToGigabytes(FrameStore.SizeOf(spec.Shape, spec.Layers, spec.StoredFrameCount));
			if (!options.AllowDiskCache)
			{
				Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
					$"{options.Name}: the run's {spec.StoredFrameCount} frames need {needed:0.0} GB and {FrameMemory.ToGigabytes(budget):0.0} GB of memory are free for them. An earlier last frame shortens the run; --allow-disk-cache lets the rest go to the frame cache on disk."));
				return 3;
			}
			FrameCache.CheckSpace(FrameStore.SizeOf(spec.Shape, spec.Layers, spec.StoredFrameCount - ramFrames));
		}
		Directory.CreateDirectory(options.Out);
		using FrameStore store = FrameStore.Create(FrameCache.NewPath(), spec.Shape, spec.Layers, spec.StoredFrameCount, spec.CaptureEvery, budget);
		using var run = new SimulationRun(spec, store, null);
		run.Start();
		run.Completion.Wait();
		if (run.State != RunState.Completed)
		{
			Console.Error.WriteLine($"{options.Name}: the run ended {run.State}{(run.Error != null ? ": " + run.Error.Message : "")}");
			return 1;
		}

		// the range in frames, from the project's export range as the app takes it
		Display display = project.Display;
		int every = spec.VisualizeEvery;
		int lastFrame = spec.FrameCount - 1;
		int first = Math.Clamp(display.ExportFrom / every, 0, lastFrame);
		int last = display.ExportTo <= 0 ? lastFrame : Math.Clamp(display.ExportTo / every, first, lastFrame);
		// the targets as the app's export makes them: the layer's colours and outline, what the WPF head draws over the
		// frames, and ffmpeg where the app has a video file
		byte[]? edges = null;
		if ((display.Field.OverlayInExport || display.Intensity.OverlayInExport) && pixels != null && pixels.Width == spec.Width && pixels.Height == spec.Height)
		{
			edges = GlowingEdges.Compute(pixels.Bgra, pixels.Width, pixels.Height, pixels.StrideBytes);
		}
		IFrameDecorator decorator = new WpfFrameDecorators().Create(project, run.Spec, run.Traces);
		var sinks = new List<FfmpegSink>();
		var targets = new List<ExportTarget>();
		foreach (FrameLayer layer in new[] { FrameLayer.Field, FrameLayer.Intensity })
		{
			DisplayLayer settings = layer == FrameLayer.Field ? display.Field : display.Intensity;
			ColorLut lut = ColorSchemeCatalog.Instance.LutFor(project, settings.ColorScheme, settings.BlackLevel);
			var exportLayer = new ExportLayer(layer, lut, (float)settings.Brightness, settings.OverlayInExport ? edges : null, settings.OverlayGain, decorator.For(layer));
			string output = Path.Combine(options.Out, options.Name + (layer == FrameLayer.Field ? "_field" : "_intensity") + ".webp");
			targets.Add(new ExportTarget(exportLayer, output, () =>
			{
				// opened by the job on its sink thread
				var sink = new FfmpegSink(options.Ffmpeg, output, spec.Width, spec.Height, display.Video.Fps * options.Speed, options.Width, options.Fps, options.Quality);
				lock (sinks)
				{
					sinks.Add(sink);
				}
				return sink;
			}));
		}
		var stopwatch = Stopwatch.StartNew();
		var job = new ExportJob(store, first, last, targets, null);
		job.Start();
		job.Completion.Wait();
		// the job closes its sinks however it ends; what ffmpeg said tells why a file is not there
		bool written = job.State == ExportState.Completed && sinks.Count == targets.Count;
		foreach (FfmpegSink sink in sinks)
		{
			if (sink.ExitCode != 0 || !File.Exists(sink.Output))
			{
				written = false;
				Console.Error.WriteLine($"{options.Name}: ffmpeg did not write {Path.GetFileName(sink.Output)} (exit code {sink.ExitCode}){(sink.Errors.Length > 0 ? ": " + sink.Errors : "")}");
			}
		}
		if (!written)
		{
			Console.Error.WriteLine($"{options.Name}: the export ended {job.State}{(job.Error != null ? ": " + job.Error.Message : "")}");
			return 1;
		}
		Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
			$"{options.Name}: {spec.Width} x {spec.Height}, frames {first} to {last} at {display.Video.Fps} fps; run {run.Elapsed.TotalSeconds:0.0} s on {run.EngineDescription} ({FrameMemory.ToGigabytes(store.RamBytes):0.0} GB of frames in memory), frames {stopwatch.Elapsed.TotalSeconds:0.0} s"));
		return 0;
	}
}
