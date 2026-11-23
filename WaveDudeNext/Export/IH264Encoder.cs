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

namespace WaveDudeNext.Export;

/// <summary>
/// What an H.264 encoder is asked for, the same of every platform's: a picture of an even size (4:2:0 has half the
/// chroma in both directions), the frame rate and the project's quality. Every encoder is also asked for no B-frames,
/// so the frames come out in the order they went in, and for a key frame a second, so the file scrubs cleanly; that is
/// what the Media Foundation writer of Windows asks for too.
/// </summary>
/// <param name="Width">Width of the encoded picture in pixels, even.</param>
/// <param name="Height">Height of the encoded picture in pixels, even.</param>
/// <param name="FramesPerSecond">Playback frame rate, at least 1; also the distance of the key frames.</param>
/// <param name="Quality">The project's quality, 1..100.</param>
public sealed record H264Settings(int Width, int Height, int FramesPerSecond, int Quality)
{
	/// <summary>The average bitrate in bits per second the author's rule gives these settings (<see cref="BitrateFor"/>), for an encoder that takes no quality.</summary>
	public int Bitrate => BitrateFor(Width, Height, FramesPerSecond, Quality);

	/// <summary>
	/// The author's bitrate rule: 0.01 × 50^(quality/100) bits per pixel (0.15 at 70), held between 300 kbit/s and
	/// 200 Mbit/s. Every step of 17.7 in the quality doubles it.
	/// </summary>
	/// <param name="width">Width of the encoded picture in pixels.</param>
	/// <param name="height">Height of the encoded picture in pixels.</param>
	/// <param name="framesPerSecond">Playback frame rate.</param>
	/// <param name="quality">The project's quality, 1..100.</param>
	/// <returns>Bits per second.</returns>
	public static int BitrateFor(int width, int height, int framesPerSecond, int quality)
	{
		double bitsPerPixel = 0.01 * Math.Pow(50.0, quality / 100.0);
		long bitrate = (long)Math.Round((double)width * height * framesPerSecond * bitsPerPixel);
		return (int)Math.Clamp(bitrate, 300_000L, 200_000_000L);
	}
}

/// <summary>
/// Takes what an <see cref="IH264Encoder"/> produces: the stream's parameter sets once, then its frames in the order
/// they were given to the encoder. The in-house MP4 container (<see cref="Mp4ContainerWriter"/>) is one. Called by the
/// encoder, on whatever thread it produces on (a thread of its own, or the caller's inside
/// <see cref="IH264Encoder.Encode"/> and <see cref="IH264Encoder.Finish"/>), one call at a time.
/// </summary>
public interface IH264Receiver
{
	/// <summary>
	/// The stream's sequence and picture parameter set, each one NAL unit with its header byte and without a start
	/// code or a length; called once, before the first <see cref="Frame"/>.
	/// </summary>
	/// <param name="sequence">The sequence parameter set.</param>
	/// <param name="picture">The picture parameter set.</param>
	void ParameterSets(ReadOnlySpan<byte> sequence, ReadOnlySpan<byte> picture);

	/// <summary>
	/// One encoded frame: its NAL units, each preceded by its length as four bytes, big-endian, which is the form an
	/// MP4 stores; no parameter sets and no delimiters among them.
	/// </summary>
	/// <param name="units">The frame's NAL units with their lengths.</param>
	/// <param name="keyFrame">Whether a decoder can start at this frame.</param>
	void Frame(ReadOnlySpan<byte> units, bool keyFrame);
}

/// <summary>
/// One H.264 encoder of a platform, behind the one seam every platform's fits (2026-11-15, the user's design: each
/// platform's own encoder before any encoder of ours): frames in as NV12, encoded frames out to the
/// <see cref="IH264Receiver"/> it was made with. The system's ffmpeg is one (<see cref="FfmpegH264Encoder"/>); the
/// Media Foundation of Windows is none, since its sink writer writes the whole file (<see cref="Mp4Writer"/>).
/// One thread at a time drives it; it may deliver on another.
/// </summary>
public interface IH264Encoder
{
	/// <summary>What encodes, for the log: the program or interface and the encoder it chose.</summary>
	string Description { get; }

	/// <summary>
	/// Takes the next frame: the Y plane's rows, then the Cb and Cr samples interleaved at half the size in both
	/// directions, studio range, without padding (width × height × 3 / 2 bytes). May block while the encoder is
	/// busy; the frame's encoded form may reach the receiver later, by <see cref="Finish"/> at the latest.
	/// </summary>
	/// <param name="nv12">The frame as NV12.</param>
	/// <exception cref="IOException">The encoder failed or has gone.</exception>
	void Encode(ReadOnlySpan<byte> nv12);

	/// <summary>
	/// Ends the frames: returns when the receiver has every frame given, and the encoder is released. Idempotent.
	/// </summary>
	/// <exception cref="IOException">The encoder failed; the receiver has the frames it got until then.</exception>
	void Finish();
}

/// <summary>
/// The H.264 encoder of a head whose platform has one the library cannot reach itself (2026-11-21: the browser head,
/// whose browser encodes through WebCodecs, an interface of the page's scripts). The head sets it once at its start
/// (<see cref="ExportFormats.PlatformH264"/>), as it sets its GPU engine at the engine factory, and an MP4 is then
/// written through it with the in-house container (<see cref="EncoderMp4Writer"/>).
/// </summary>
/// <param name="Description">What encodes, for the About box: the interface and what is known of its encoder.</param>
/// <param name="Open">
/// Makes the encoder for the settings, delivering to the receiver given; a <see cref="NotSupportedException"/> with
/// the reason where the platform's encoder does not take these settings (a picture too large for it), an
/// <see cref="IOException"/> where it could not be started. Any thread.
/// </param>
public sealed record PlatformH264Encoder(string Description, Func<H264Settings, IH264Receiver, IH264Encoder> Open);
