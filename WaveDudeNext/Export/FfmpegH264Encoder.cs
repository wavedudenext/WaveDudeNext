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
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NLog;

namespace WaveDudeNext.Export;

/// <summary>What asking the system's ffmpeg gave: whether it is there, its version and the H.264 encoder it has.</summary>
/// <param name="Found">Whether an ffmpeg on the path started.</param>
/// <param name="Version">The version it names in its banner; empty when not found or not named.</param>
/// <param name="Encoder">The H.264 encoder chosen of those it lists (<see cref="FfmpegH264Encoder.Preferred"/>); null when it has none of them.</param>
public sealed record FfmpegProbe(bool Found, string Version, string? Encoder)
{
	/// <summary>What keeps this ffmpeg from encoding H.264; null when nothing does.</summary>
	public ExportObstacle? Obstacle => !Found ? ExportObstacle.FfmpegMissing : Encoder == null ? ExportObstacle.FfmpegWithoutH264 : null;

	/// <summary>The program, its version and its encoder, for the log and the About window: "ffmpeg 6.1.1 (libx264)".</summary>
	public string Description => (Version.Length > 0 ? FfmpegH264Encoder.Program + " " + Version : FfmpegH264Encoder.Program) + (Encoder != null ? " (" + Encoder + ")" : "");
}

/// <summary>
/// The system's ffmpeg as an H.264 encoder (2026-11-15, the user's choice for Linux, which has no encoder of its
/// own: the program on the path, nothing of it shipped, and a message naming it when it is missing). ffmpeg is the
/// encoder alone: the frames go into its standard input as raw NV12, the raw H.264 stream comes out of its standard
/// output (his choice of two, over ffmpeg's own fragmented MP4: an ordinary MP4 needs a file ffmpeg can seek in) and
/// is cut into frames here (<see cref="H264AnnexBSplitter"/>) for the receiver, the in-house container. It encodes
/// with libx264 where it has it, at a constant quality from the project's (<see cref="Crf"/>), else with libopenh264
/// at the author's bitrate; no B-frames, a key frame a second, the stream tagged with the matrix and range the planes
/// are converted with. Nothing here is Linux's alone: it runs wherever an ffmpeg is on the path. One thread at a time
/// drives it; a thread of its own reads ffmpeg's output and delivers the frames.
/// </summary>
public sealed class FfmpegH264Encoder : IH264Encoder
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(FfmpegH264Encoder).FullName!);

	/// <summary>The program asked for, found through the path.</summary>
	public const string Program = "ffmpeg";

	/// <summary>The H.264 encoders of ffmpeg this takes, the first it has: x264, then Cisco's OpenH264 (what a distribution without x264 builds in).</summary>
	public static readonly IReadOnlyList<string> Preferred = ["libx264", "libopenh264"];

	/// <summary>A probe that found an encoder, kept for the process's life; one that found none is made again, so an ffmpeg installed meanwhile is found.</summary>
	private static FfmpegProbe? _known;

	private readonly Process _process;
	private readonly Stream _input;
	private readonly Stream _output;
	private readonly H264AnnexBSplitter _splitter;
	private readonly Thread _reader;
	private readonly StringBuilder _errors = new();
	private volatile Exception? _failure;
	private bool _finished;

	/// <summary>Starts ffmpeg for the settings.</summary>
	/// <param name="settings">The picture's size, the frame rate and the quality.</param>
	/// <param name="receiver">Where the encoded frames go; called from the thread that reads ffmpeg's output.</param>
	/// <exception cref="NotSupportedException">No ffmpeg on the path, or one without an H.264 encoder.</exception>
	/// <exception cref="IOException">ffmpeg could not be started.</exception>
	public FfmpegH264Encoder(H264Settings settings, IH264Receiver receiver)
	{
		FfmpegProbe probe = Probe();
		if (probe.Obstacle is { } obstacle)
		{
			throw new NotSupportedException(obstacle == ExportObstacle.FfmpegMissing
				? "MP4 export needs ffmpeg, which was not found on the path."
				: "The ffmpeg on the path has no H.264 encoder (libx264 or libopenh264).");
		}
		Description = probe.Description;
		var start = new ProcessStartInfo(Program)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		string[] arguments = Arguments(settings, probe.Encoder!);
		foreach (string argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}
		Logger.Debug("Starting {Program} {Arguments}", Program, string.Join(' ', arguments));
		try
		{
			_process = Process.Start(start) ?? throw new IOException("ffmpeg did not start.");
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
		{
			throw new IOException("ffmpeg could not be started: " + ex.Message, ex);
		}
		// what ffmpeg says is read as it comes: a full pipe nobody reads would stop it
		_process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				lock (_errors)
				{
					_errors.AppendLine(e.Data);
				}
			}
		};
		_process.BeginErrorReadLine();
		_input = _process.StandardInput.BaseStream;
		_output = _process.StandardOutput.BaseStream;
		_splitter = new H264AnnexBSplitter(receiver);
		_reader = new Thread(Read) { IsBackground = true, Name = "ffmpeg H.264 output" };
		_reader.Start();
	}

	/// <summary>Makes the encoder; the form an <see cref="EncoderMp4Writer"/> takes.</summary>
	/// <param name="settings">The picture's size, the frame rate and the quality.</param>
	/// <param name="receiver">Where the encoded frames go.</param>
	/// <exception cref="NotSupportedException">No ffmpeg on the path, or one without an H.264 encoder.</exception>
	/// <exception cref="IOException">ffmpeg could not be started.</exception>
	public static IH264Encoder Open(H264Settings settings, IH264Receiver receiver) => new FfmpegH264Encoder(settings, receiver);

	/// <inheritdoc/>
	public string Description { get; }

	/// <summary>What ffmpeg wrote to its error stream so far (it is started to report errors only).</summary>
	public string Errors
	{
		get
		{
			lock (_errors)
			{
				return _errors.ToString().Trim();
			}
		}
	}

	/// <summary>
	/// Asks the ffmpeg on the path what it is and which H.264 encoders it has: one start of it, about a tenth of a
	/// second, its answer kept once it has an encoder. Any thread.
	/// </summary>
	public static FfmpegProbe Probe()
	{
		if (_known is { } known)
		{
			return known;
		}
		FfmpegProbe probe = Ask();
		if (probe.Encoder != null)
		{
			_known = probe;
		}
		Logger.Debug("H.264 through ffmpeg: {Answer}", !probe.Found ? "no ffmpeg on the path" : probe.Encoder == null ? probe.Description + " has none of " + string.Join(", ", Preferred) : probe.Description);
		return probe;
	}

	private static FfmpegProbe Ask()
	{
		var start = new ProcessStartInfo(Program)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		start.ArgumentList.Add("-encoders");
		try
		{
			using Process process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
			// the banner with the version goes to the error stream, the list to the output
			Task<string> banner = process.StandardError.ReadToEndAsync();
			string listing = process.StandardOutput.ReadToEnd();
			if (!process.WaitForExit(10_000))
			{
				process.Kill();
				return new FfmpegProbe(false, "", null);
			}
			return new FfmpegProbe(true, VersionOf(banner.Result), ChooseEncoder(listing));
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
		{
			Logger.Debug("ffmpeg did not start: {Message}", ex.Message);
			return new FfmpegProbe(false, "", null);
		}
	}

	/// <summary>The version ffmpeg's banner names: the word behind "ffmpeg version"; empty when the banner has none.</summary>
	/// <param name="banner">What ffmpeg writes first to its error stream.</param>
	public static string VersionOf(string banner)
	{
		const string lead = "ffmpeg version ";
		int at = banner.IndexOf(lead, StringComparison.Ordinal);
		if (at < 0)
		{
			return "";
		}
		int from = at + lead.Length;
		int to = banner.IndexOfAny([' ', '\r', '\n'], from);
		return (to < 0 ? banner[from..] : banner[from..to]).Trim();
	}

	/// <summary>The first of the <see cref="Preferred"/> encoders that ffmpeg's list of encoders names as a video encoder; null when it names none.</summary>
	/// <param name="listing">The output of <c>ffmpeg -encoders</c>: a line per encoder, its flags (V for video first), its name, its description.</param>
	public static string? ChooseEncoder(string listing)
	{
		var video = new HashSet<string>(StringComparer.Ordinal);
		foreach (string line in listing.Split('\n'))
		{
			string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (words.Length >= 2 && words[0].StartsWith('V'))
			{
				video.Add(words[1]);
			}
		}
		foreach (string encoder in Preferred)
		{
			if (video.Contains(encoder))
			{
				return encoder;
			}
		}
		return null;
	}

	/// <summary>
	/// x264's constant rate factor for the project's quality: 20 at 70, and from there the author's bitrate rule in
	/// x264's terms, where six steps halve the bitrate (10 at quality 100, 27 at 50, 43 at 1); 0..51.
	/// </summary>
	/// <param name="quality">The project's quality, 1..100.</param>
	public static int Crf(int quality) => (int)Math.Clamp(Math.Round(20 - 6 * Math.Log2(50) / 100 * (quality - 70)), 0, 51);

	/// <summary>
	/// ffmpeg's command line: raw NV12 frames from its standard input, the encoder with its quality, no B-frames
	/// where it could make them, a key frame a second, the BT.601 matrix and the studio range named on both sides
	/// (so nothing is converted and the stream says what it holds), raw H.264 to its standard output.
	/// </summary>
	/// <param name="settings">The picture's size, the frame rate and the quality.</param>
	/// <param name="encoder">One of the <see cref="Preferred"/> encoders.</param>
	public static string[] Arguments(H264Settings settings, string encoder)
	{
		string fps = settings.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
		List<string> arguments =
		[
			"-hide_banner", "-v", "error",
			"-f", "rawvideo", "-pix_fmt", "nv12", "-video_size", string.Create(CultureInfo.InvariantCulture, $"{settings.Width}x{settings.Height}"), "-framerate", fps,
			"-colorspace", "smpte170m", "-color_range", "tv", "-i", "pipe:0",
			"-an", "-c:v", encoder,
		];
		if (encoder == "libx264")
		{
			arguments.AddRange(["-preset", "medium", "-crf", Crf(settings.Quality).ToString(CultureInfo.InvariantCulture), "-bf", "0"]);
		}
		else
		{
			arguments.AddRange(["-b:v", settings.Bitrate.ToString(CultureInfo.InvariantCulture)]);
		}
		arguments.AddRange(["-g", fps, "-colorspace", "smpte170m", "-color_range", "tv", "-f", "h264", "pipe:1"]);
		return [.. arguments];
	}

	/// <inheritdoc/>
	/// <exception cref="InvalidOperationException">The encoder has finished.</exception>
	public void Encode(ReadOnlySpan<byte> nv12)
	{
		if (_finished)
		{
			throw new InvalidOperationException("The encoder has finished.");
		}
		ThrowIfFailed();
		try
		{
			_input.Write(nv12);
		}
		catch (IOException ex)
		{
			// ffmpeg has gone: its own words say why, once it has said them
			_process.WaitForExit(2000);
			ThrowIfFailed();
			throw new IOException("ffmpeg stopped taking frames: " + Errors, ex);
		}
	}

	/// <inheritdoc/>
	public void Finish()
	{
		if (_finished)
		{
			return;
		}
		_finished = true;
		try
		{
			_input.Close();
		}
		catch (IOException)
		{
			// ffmpeg ended before its input did; its exit code and its words tell
		}
		_reader.Join();
		_process.WaitForExit();
		int exitCode = _process.ExitCode;
		_process.Dispose();
		ThrowIfFailed();
		if (exitCode != 0)
		{
			throw new IOException(string.Create(CultureInfo.InvariantCulture, $"ffmpeg ended with exit code {exitCode}: {Errors}"));
		}
	}

	/// <summary>The reading thread: ffmpeg's output into the splitter until it ends; a failure of the receiver ends ffmpeg, which would otherwise wait for a reader for ever.</summary>
	private void Read()
	{
		var buffer = new byte[1 << 16];
		try
		{
			int read;
			while ((read = _output.Read(buffer, 0, buffer.Length)) > 0)
			{
				_splitter.Push(buffer.AsSpan(0, read));
			}
			_splitter.Complete();
		}
		catch (Exception ex)
		{
			_failure = ex;
			try
			{
				_process.Kill();
			}
			catch (Exception killing) when (killing is InvalidOperationException or Win32Exception)
			{
				// it had ended already
			}
		}
	}

	private void ThrowIfFailed()
	{
		if (_failure is { } failure)
		{
			throw new IOException("The H.264 stream of ffmpeg could not be written: " + failure.Message, failure);
		}
	}
}
