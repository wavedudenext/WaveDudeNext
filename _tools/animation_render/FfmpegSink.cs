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
using System.Runtime.InteropServices;
using System.Text;
using WaveDudeNext.Export;

namespace AnimationRender;

/// <summary>
/// An export output that is an ffmpeg process: every rendered frame goes into its standard input as it is, packed
/// BGRA32 at the canvas's size, so nothing lies between the renderer's pixels and ffmpeg's scaler, no file and no
/// codec; ffmpeg picks the frames of the animation's rate, scales them with Lanczos and writes a looping animated
/// WebP of lossy frames. One thread at a time, the export job's sink thread, which opens, feeds and closes it.
/// </summary>
internal sealed class FfmpegSink : IFrameSink
{
	private readonly Process _process;
	private readonly Stream _input;
	private readonly StringBuilder _errors = new();
	private bool _closed;

	/// <summary>Starts ffmpeg.</summary>
	/// <param name="ffmpeg">The ffmpeg executable, a path or a name on the path; it needs the libwebp encoder.</param>
	/// <param name="output">The WebP file to write; replaced when there.</param>
	/// <param name="width">Width of the frames given, in pixels.</param>
	/// <param name="height">Height of the frames given, in pixels.</param>
	/// <param name="inputFps">Frames per second the frames given are played at: the project's video frame rate times the speed.</param>
	/// <param name="outputWidth">Width of the animation in pixels; its height follows from the frames' shape.</param>
	/// <param name="outputFps">Frames per second of the animation; ffmpeg picks the frames nearest in time.</param>
	/// <param name="quality">Quality of the lossy WebP frames, 1 to 100.</param>
	/// <exception cref="IOException">ffmpeg could not be started.</exception>
	public FfmpegSink(string ffmpeg, string output, int width, int height, double inputFps, int outputWidth, int outputFps, int quality)
	{
		Width = width;
		Height = height;
		Output = output;
		var start = new ProcessStartInfo(ffmpeg)
		{
			RedirectStandardInput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		// the alpha byte of the frames means nothing (bgr0); the frame picking comes before the scaling, so only the
		// frames kept are scaled
		string[] arguments =
		[
			"-hide_banner", "-v", "error", "-y",
			"-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", $"{width}x{height}",
			"-framerate", inputFps.ToString("0.###", CultureInfo.InvariantCulture), "-i", "pipe:0",
			"-an", "-vf", $"fps={outputFps},scale={outputWidth}:-1:flags=lanczos",
			"-c:v", "libwebp", "-lossless", "0", "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-compression_level", "6", "-loop", "0",
			output,
		];
		foreach (string argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}
		try
		{
			_process = Process.Start(start) ?? throw new IOException($"'{ffmpeg}' did not start.");
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			throw new IOException($"'{ffmpeg}' could not be started: {ex.Message}", ex);
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
	}

	/// <inheritdoc/>
	public int Width { get; }

	/// <inheritdoc/>
	public int Height { get; }

	/// <inheritdoc/>
	public int FrameCount { get; private set; }

	/// <inheritdoc/>
	public string Output { get; }

	/// <summary>ffmpeg's exit code once <see cref="Close"/> has returned; 0 is success. -1 before.</summary>
	public int ExitCode { get; private set; } = -1;

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

	/// <inheritdoc/>
	public void Add(ReadOnlySpan<int> pixels)
	{
		if (_closed)
		{
			throw new InvalidOperationException("The sink is closed.");
		}
		try
		{
			_input.Write(MemoryMarshal.AsBytes(pixels[..(Width * Height)]));
		}
		catch (IOException ex)
		{
			// ffmpeg has gone: its own words say why
			throw new IOException($"ffmpeg stopped taking frames after {FrameCount}: {Errors}", ex);
		}
		FrameCount++;
	}

	/// <summary>Ends the frames and waits for ffmpeg to finish the file; <see cref="ExitCode"/> tells how it went. Idempotent.</summary>
	public void Close()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		try
		{
			_input.Close();
		}
		catch (IOException)
		{
			// ffmpeg ended before its input did; its exit code and its words tell
		}
		_process.WaitForExit();
		ExitCode = _process.ExitCode;
		_process.Dispose();
	}

	/// <inheritdoc/>
	public void Dispose() => Close();
}
