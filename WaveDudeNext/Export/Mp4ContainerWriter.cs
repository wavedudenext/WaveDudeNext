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

using System.Buffers.Binary;
using System.Text;

namespace WaveDudeNext.Export;

/// <summary>
/// Writes the MP4 container around an H.264 stream that an <see cref="IH264Encoder"/> produces: the in-house half of
/// an MP4 wherever the platform's encoder hands back encoded frames and no file (2026-11-15, the user's choice; on
/// Windows the Media Foundation writes both halves, <see cref="Mp4Writer"/>). An ordinary, unfragmented MP4 (ISO base
/// media file format, the QuickTime container's descendant, so the layout is <see cref="MovWriter"/>'s): <c>ftyp</c>,
/// then room for the <c>moov</c> with the sample tables, reserved as a <c>free</c> box by the planned frame count (a
/// fixed part plus 16 bytes a frame), then the frames in one <c>mdat</c> as they come; at <see cref="Close"/> the
/// reservation is overwritten with the real <c>moov</c>, so a player need not read the whole file before the first
/// frame. More frames than planned, or none planned, put the <c>moov</c> at the end. The <c>mdat</c> size and the
/// chunk offsets are 64-bit (<c>co64</c>). One video track, <c>avc1</c>: the parameter sets in its <c>avcC</c>, the
/// matrix and range of its planes in a <c>colr</c>, every frame a sample of one time unit, the key frames listed in
/// <c>stss</c>; no B-frames, so no composition offsets.
/// Needs a stream it can seek in. One thread at a time: the encoder's while frames come, then the closing one.
/// </summary>
public sealed class Mp4ContainerWriter : IH264Receiver
{
	/// <summary>The moov's bytes besides the per-frame tables and the parameter sets (about 700 as written), with slack for the sets.</summary>
	private const int MoovFixedBytes = 2048;

	/// <summary>The moov's bytes a frame: its size (4), its offset (8), and its number when it is a key frame (4).</summary>
	private const int MoovBytesPerFrame = 16;

	private readonly Stream _stream;
	private readonly long _reservedStart;
	private readonly long _reservedBytes;
	private readonly long _mdatStart;
	private readonly List<long> _offsets = [];
	private readonly List<int> _sizes = [];
	/// <summary>The key frames by sample number, counted from 1 as the container counts.</summary>
	private readonly List<int> _keyFrames = [];
	private byte[]? _sequence;
	private byte[]? _picture;
	private bool _closed;

	/// <param name="stream">Where the file goes: writable, seekable, empty and at its start; closed with the writer.</param>
	/// <param name="width">Width of the encoded picture in pixels.</param>
	/// <param name="height">Height of the encoded picture in pixels.</param>
	/// <param name="framesPerSecond">Playback rate; the file's time scale, a frame one unit of it.</param>
	/// <param name="plannedFrames">How many frames will come at most, which sizes the reservation for the leading <c>moov</c>; 0 puts it at the end.</param>
	/// <exception cref="ArgumentException">The stream cannot be written or cannot seek.</exception>
	public Mp4ContainerWriter(Stream stream, int width, int height, int framesPerSecond, int plannedFrames = 0)
	{
		if (!stream.CanWrite || !stream.CanSeek)
		{
			throw new ArgumentException("An MP4 needs a stream it can write to and seek in: the index is written over a reservation at the front.", nameof(stream));
		}
		_stream = stream;
		Width = width;
		Height = height;
		FramesPerSecond = Math.Max(1, framesPerSecond);
		PlannedFrames = Math.Max(0, plannedFrames);
		// what the file is: the base format, its second edition, H.264 in it, MP4 version 1
		_stream.Write(Box("ftyp", [.. "isom"u8, 0, 0, 2, 0, .. "isom"u8, .. "iso2"u8, .. "avc1"u8, .. "mp41"u8]));
		_reservedStart = _stream.Position;
		if (PlannedFrames > 0)
		{
			// room for the moov of the planned frames, plus a free box's head behind it, as one free box for now
			_reservedBytes = MoovFixedBytes + (long)MoovBytesPerFrame * PlannedFrames + 8;
			_stream.Write(FreeBoxHead(_reservedBytes));
			_stream.Position = _reservedStart + _reservedBytes;
		}
		_mdatStart = _stream.Position;
		Span<byte> mdat = stackalloc byte[16];
		BinaryPrimitives.WriteUInt32BigEndian(mdat, 1); // size 1: a 64-bit size follows the type, patched at Close
		"mdat"u8.CopyTo(mdat[4..]);
		BinaryPrimitives.WriteUInt64BigEndian(mdat[8..], 0);
		_stream.Write(mdat);
	}

	/// <summary>Width of the encoded picture in pixels.</summary>
	public int Width { get; }

	/// <summary>Height of the encoded picture in pixels.</summary>
	public int Height { get; }

	/// <summary>Frames per second, the file's time scale.</summary>
	public int FramesPerSecond { get; }

	/// <summary>The frame count the leading moov was reserved for; 0 for a trailing moov.</summary>
	public int PlannedFrames { get; }

	/// <summary>Frames written so far.</summary>
	public int FrameCount => _sizes.Count;

	/// <summary>Bytes of encoded frames written so far.</summary>
	public long FrameBytes { get; private set; }

	/// <summary>Whether the moov was written at the front (the reservation held it); known after <see cref="Close"/>.</summary>
	public bool MoovFirst { get; private set; }

	/// <inheritdoc/>
	public void ParameterSets(ReadOnlySpan<byte> sequence, ReadOnlySpan<byte> picture)
	{
		if (sequence.Length > ushort.MaxValue || picture.Length > ushort.MaxValue)
		{
			throw new InvalidDataException("An H.264 parameter set is longer than an MP4 can hold.");
		}
		_sequence ??= sequence.ToArray();
		_picture ??= picture.ToArray();
	}

	/// <inheritdoc/>
	/// <exception cref="InvalidOperationException">The file is closed.</exception>
	/// <exception cref="IOException">The stream could not be written.</exception>
	public void Frame(ReadOnlySpan<byte> units, bool keyFrame)
	{
		if (_closed)
		{
			throw new InvalidOperationException("The MP4 file is already closed.");
		}
		_offsets.Add(_stream.Position);
		_stream.Write(units);
		_sizes.Add(units.Length);
		FrameBytes += units.Length;
		if (keyFrame)
		{
			_keyFrames.Add(_sizes.Count);
		}
	}

	/// <summary>Finishes the file: the moov over its reservation or at the end, the mdat's size, and the stream closed. Idempotent.</summary>
	/// <exception cref="IOException">The stream could not be written.</exception>
	public void Close()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		try
		{
			long mdatEnd = _stream.Position;
			byte[] moov = BuildMoov();
			if (_reservedBytes > 0 && moov.Length + 8 <= _reservedBytes)
			{
				// the real moov over the reservation, a free box over what is left of it
				_stream.Position = _reservedStart;
				_stream.Write(moov);
				_stream.Write(FreeBoxHead(_reservedBytes - moov.Length));
				MoovFirst = true;
			}
			else
			{
				_stream.Position = mdatEnd;
				_stream.Write(moov);
			}
			_stream.Position = _mdatStart + 8;
			Span<byte> size = stackalloc byte[8];
			BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)(mdatEnd - _mdatStart));
			_stream.Write(size);
			_stream.Flush();
		}
		finally
		{
			_stream.Dispose();
		}
	}

	// ---------------------------------------------------------------- the movie boxes

	private byte[] BuildMoov()
	{
		uint timescale = (uint)FramesPerSecond;
		uint duration = (uint)_sizes.Count;
		return Box("moov", [.. Mvhd(timescale, duration), .. Box("trak", [.. Tkhd(duration), .. Box("mdia", [.. Mdhd(timescale, duration), .. Hdlr(), .. Box("minf",
			[.. Box("vmhd", [0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0]), .. Box("dinf", Box("dref", [0, 0, 0, 0, 0, 0, 0, 1, .. Box("url ", [0, 0, 0, 1])])),
			.. Box("stbl", [.. Stsd(), .. Stts(), .. Stss(), .. Box("stsc", [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]), .. Stsz(), .. Co64()])])])])]);
	}

	private static byte[] Mvhd(uint timescale, uint duration)
	{
		var b = new byte[100];
		// version 0, flags 0, creation and modification time 0, timescale, duration
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), timescale);
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), duration);
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 0x00010000); // rate 1.0
		BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(24), 0x0100); // volume 1.0
		Matrix(b.AsSpan(36));
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(96), 2); // next track id
		return Box("mvhd", b);
	}

	private byte[] Tkhd(uint duration)
	{
		var b = new byte[84];
		b[3] = 0x03; // enabled, in the movie
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 1); // track id
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), duration);
		Matrix(b.AsSpan(40));
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(76), (uint)Width << 16);
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(80), (uint)Height << 16);
		return Box("tkhd", b);
	}

	private static byte[] Mdhd(uint timescale, uint duration)
	{
		var b = new byte[24];
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), timescale);
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), duration);
		BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), 0x55C4); // language: undetermined
		return Box("mdhd", b);
	}

	/// <summary>The handler reference the ISO way: the handler type 'vide' and a name ending in a zero byte.</summary>
	private static byte[] Hdlr() => Box("hdlr", [0, 0, 0, 0, 0, 0, 0, 0, .. "vide"u8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "VideoHandler"u8, 0]);

	/// <summary>The sample description: one <c>avc1</c> entry, 24-bit, with the decoder configuration; none while no parameter sets have come.</summary>
	private byte[] Stsd()
	{
		if (_sequence == null || _picture == null)
		{
			return Box("stsd", [0, 0, 0, 0, 0, 0, 0, 0]);
		}
		var e = new byte[86];
		"avc1"u8.CopyTo(e.AsSpan(4));
		BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(14), 1); // data reference index
		BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(32), (ushort)Width);
		BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(34), (ushort)Height);
		BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(36), 0x00480000); // 72 dpi
		BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(40), 0x00480000);
		BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(48), 1); // frames per sample
		BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(82), 24); // depth
		BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(84), 0xFFFF); // no colour table
		byte[] entry = [.. e, .. Box("avcC", DecoderConfiguration(_sequence, _picture)), .. Colr()];
		BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)entry.Length);
		return Box("stsd", [0, 0, 0, 0, 0, 0, 0, 1, .. entry]);
	}

	/// <summary>
	/// The colour information of the sample entry (<c>colr</c>, the <c>nclx</c> form, by the numbers of ITU-T H.273):
	/// what the planes every encoder here is given were made with (<see cref="EncoderMp4Writer"/>), the BT.601 matrix
	/// (6) and the studio range, the primaries and the transfer curve unspecified (2), which is what ffmpeg's stream
	/// says of itself. In the container because not every encoder says it in its stream (2026-11-21: a browser's
	/// writes no such thing, and a player takes an untagged HD picture for BT.709, its colours slightly off).
	/// </summary>
	private static byte[] Colr() => Box("colr", [.. "nclx"u8, 0, 2, 0, 2, 0, 6, 0]);

	/// <summary>
	/// The AVC decoder configuration record: the profile, its constraint flags and the level as the sequence parameter
	/// set has them, NAL unit lengths of four bytes, the two parameter sets, and for the profiles from High on the
	/// chroma format and the sample depths.
	/// </summary>
	/// <param name="sequence">The sequence parameter set, header byte first.</param>
	/// <param name="picture">The picture parameter set, header byte first.</param>
	/// <exception cref="InvalidDataException">The sequence parameter set cannot be read.</exception>
	public static byte[] DecoderConfiguration(ReadOnlySpan<byte> sequence, ReadOnlySpan<byte> picture)
	{
		H264SequenceFacts facts = H264SequenceFacts.Read(sequence);
		byte[] head =
		[
			1, (byte)facts.Profile, (byte)facts.Compatibility, (byte)facts.Level,
			0xFF, // six reserved bits, then the length's size less one: 3
			0xE1, // three reserved bits, then one sequence parameter set
			(byte)(sequence.Length >> 8), (byte)sequence.Length, .. sequence,
			1, // one picture parameter set
			(byte)(picture.Length >> 8), (byte)picture.Length, .. picture,
		];
		if (facts.Profile is 66 or 77 or 88)
		{
			return head;
		}
		return [.. head, (byte)(0xFC | facts.ChromaFormat), (byte)(0xF8 | (facts.LumaBits - 8)), (byte)(0xF8 | (facts.ChromaBits - 8)), 0];
	}

	/// <summary>The durations: every sample lasts one unit of the time scale.</summary>
	private byte[] Stts()
	{
		if (_sizes.Count == 0)
		{
			return Box("stts", [0, 0, 0, 0, 0, 0, 0, 0]);
		}
		var b = new byte[16];
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)_sizes.Count);
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 1);
		return Box("stts", b);
	}

	/// <summary>The key frames' sample numbers; left out when every frame is one, which is what its absence means.</summary>
	private byte[] Stss()
	{
		if (_keyFrames.Count == _sizes.Count)
		{
			return [];
		}
		var b = new byte[8 + 4 * _keyFrames.Count];
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)_keyFrames.Count);
		for (int i = 0; i < _keyFrames.Count; i++)
		{
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + 4 * i), (uint)_keyFrames[i]);
		}
		return Box("stss", b);
	}

	private byte[] Stsz()
	{
		var b = new byte[12 + 4 * _sizes.Count];
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)_sizes.Count);
		for (int i = 0; i < _sizes.Count; i++)
		{
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12 + 4 * i), (uint)_sizes[i]);
		}
		return Box("stsz", b);
	}

	private byte[] Co64()
	{
		var b = new byte[8 + 8 * _offsets.Count];
		BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)_offsets.Count);
		for (int i = 0; i < _offsets.Count; i++)
		{
			BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8 + 8 * i), (ulong)_offsets[i]);
		}
		return Box("co64", b);
	}

	/// <summary>The head of a <c>free</c> box of <paramref name="bytes"/> bytes in all (the body is whatever lies there).</summary>
	private static byte[] FreeBoxHead(long bytes)
	{
		var head = new byte[8];
		BinaryPrimitives.WriteUInt32BigEndian(head, (uint)bytes);
		"free"u8.CopyTo(head.AsSpan(4));
		return head;
	}

	/// <summary>The identity transformation matrix (16.16 and 2.30 fixed point).</summary>
	private static void Matrix(Span<byte> b)
	{
		BinaryPrimitives.WriteUInt32BigEndian(b, 0x00010000);
		BinaryPrimitives.WriteUInt32BigEndian(b[16..], 0x00010000);
		BinaryPrimitives.WriteUInt32BigEndian(b[32..], 0x40000000);
	}

	private static byte[] Box(string type, ReadOnlySpan<byte> payload)
	{
		var box = new byte[8 + payload.Length];
		BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
		Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
		payload.CopyTo(box.AsSpan(8));
		return box;
	}
}
