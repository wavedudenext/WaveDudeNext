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

using NLog;

namespace WaveDudeNext.Export;

/// <summary>
/// Writes H.264 MP4 where the platform's encoder hands back encoded frames and no file (2026-11-15, the user's
/// design): the frames go as NV12 into an <see cref="IH264Encoder"/>, the platform's own, and what it produces goes
/// into the in-house container (<see cref="Mp4ContainerWriter"/>), so every such platform writes the same file
/// around its encoder's stream. On Windows the Media Foundation does both halves (<see cref="Mp4Writer"/>). The
/// colours are converted here, by the planes the Windows writer uses (BT.601, studio range), so they are the same
/// arithmetic everywhere; odd sizes are padded by repeating the last row or column, as there. A failing
/// constructor means "no H.264 here". One thread at a time; the encoder may write the container from another
/// while frames are taken.
/// </summary>
public sealed class EncoderMp4Writer : IFrameSink
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(EncoderMp4Writer).FullName!);

	private readonly int _encodedWidth;
	private readonly int _encodedHeight;
	private readonly int[] _frame;
	private readonly YuvPlanes _planes;
	private readonly byte[] _nv12;
	private readonly Mp4ContainerWriter _container;
	private readonly IH264Encoder _encoder;
	private bool _closed;

	/// <param name="stream">Where the file goes: writable, seekable, empty and at its start; closed with the writer, and at once when the constructor fails.</param>
	/// <param name="output">What the output is called in the log: the file.</param>
	/// <param name="width">Frame width in pixels.</param>
	/// <param name="height">Frame height in pixels.</param>
	/// <param name="framesPerSecond">Playback frame rate.</param>
	/// <param name="quality">1..100: what the encoder's quality or bitrate is made from.</param>
	/// <param name="plannedFrames">How many frames will come at most (the container reserves its index at the front by it); 0 when unknown.</param>
	/// <param name="open">Makes the platform's encoder for the settings, delivering to the receiver given.</param>
	/// <exception cref="NotSupportedException">The platform has no H.264 encoder (the encoder's words say what is missing).</exception>
	/// <exception cref="IOException">The encoder could not be started.</exception>
	public EncoderMp4Writer(Stream stream, string output, int width, int height, int framesPerSecond, int quality, int plannedFrames, Func<H264Settings, IH264Receiver, IH264Encoder> open)
	{
		Width = width;
		Height = height;
		Output = output;
		Quality = Math.Clamp(quality, 1, 100);
		_encodedWidth = (width + 1) & ~1;
		_encodedHeight = (height + 1) & ~1;
		try
		{
			_frame = new int[_encodedWidth * _encodedHeight];
			_planes = new YuvPlanes(_encodedWidth, _encodedHeight, studioRange: true, subsampled: true);
			_nv12 = new byte[_encodedWidth * _encodedHeight * 3 / 2];
			_container = new Mp4ContainerWriter(stream, _encodedWidth, _encodedHeight, framesPerSecond, plannedFrames);
			_encoder = open(new H264Settings(_encodedWidth, _encodedHeight, Math.Max(1, framesPerSecond), Quality), _container);
		}
		catch
		{
			stream.Dispose();
			throw;
		}
		Logger.Debug("MP4: {Encoder}, {Width} x {Height} at {Fps} fps, quality {Quality}", _encoder.Description, _encodedWidth, _encodedHeight, framesPerSecond, Quality);
	}

	/// <summary>Opens the writer on a file; an existing one is replaced, and none is left when the constructor fails.</summary>
	/// <param name="path">Output .mp4 path.</param>
	/// <param name="width">Frame width in pixels.</param>
	/// <param name="height">Frame height in pixels.</param>
	/// <param name="framesPerSecond">Playback frame rate.</param>
	/// <param name="quality">1..100.</param>
	/// <param name="plannedFrames">How many frames will come at most; 0 when unknown.</param>
	/// <param name="open">Makes the platform's encoder.</param>
	/// <exception cref="NotSupportedException">The platform has no H.264 encoder.</exception>
	/// <exception cref="IOException">The file could not be made, or the encoder not started.</exception>
	public static EncoderMp4Writer Create(string path, int width, int height, int framesPerSecond, int quality, int plannedFrames, Func<H264Settings, IH264Receiver, IH264Encoder> open)
	{
		Stream stream = ExportFiles.Create(path);
		try
		{
			return new EncoderMp4Writer(stream, path, width, height, framesPerSecond, quality, plannedFrames, open);
		}
		catch
		{
			try
			{
				File.Delete(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.Debug("The empty file {Path} stays: {Message}", path, ex.Message);
			}
			throw;
		}
	}

	/// <inheritdoc/>
	public int Width { get; }

	/// <inheritdoc/>
	public int Height { get; }

	/// <inheritdoc/>
	public int FrameCount { get; private set; }

	/// <inheritdoc/>
	public string Output { get; }

	/// <summary>The quality 1..100 the encoder was given.</summary>
	public int Quality { get; }

	/// <summary>What encodes, as the encoder describes itself.</summary>
	public string Encoder => _encoder.Description;

	/// <inheritdoc/>
	public void Add(ReadOnlySpan<int> pixels)
	{
		if (_closed)
		{
			throw new InvalidOperationException("The video file is already closed.");
		}
		if (pixels.Length < Width * Height)
		{
			throw new ArgumentException("The frame is smaller than the video.", nameof(pixels));
		}
		// the frame at the encoded size: an odd width or height gets its last column or row again
		for (int y = 0; y < _encodedHeight; y++)
		{
			ReadOnlySpan<int> row = pixels.Slice(Math.Min(y, Height - 1) * Width, Width);
			Span<int> target = _frame.AsSpan(y * _encodedWidth, _encodedWidth);
			row.CopyTo(target);
			if (_encodedWidth > Width)
			{
				target[Width] = row[Width - 1];
			}
		}
		_planes.Convert(_frame);
		_planes.CopyNv12(_nv12);
		_encoder.Encode(_nv12);
		FrameCount++;
	}

	/// <inheritdoc/>
	public void Close()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		try
		{
			_encoder.Finish();
		}
		finally
		{
			// the container is closed whatever the encoder said: the file then holds the frames that came
			_container.Close();
			Logger.Debug("MP4 closed: {Frames} of {Given} frames in the file, {Bytes} bytes of H.264, the index {Where}", _container.FrameCount, FrameCount, _container.FrameBytes, _container.MoovFirst ? "at the front" : "at the end");
		}
	}

	/// <inheritdoc/>
	public void Dispose() => Close();
}
