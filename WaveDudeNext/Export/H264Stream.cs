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

namespace WaveDudeNext.Export;

/// <summary>
/// Cuts a raw H.264 byte stream (Annex B of the standard: NAL units behind start codes, <c>00 00 01</c>, as an
/// encoder writes them to a pipe) into frames for an <see cref="IH264Receiver"/>. The bytes come in pieces of any
/// size (<see cref="Push"/>); a frame is complete when the next one begins, so the last comes with
/// <see cref="Complete"/>. A frame begins at a slice that holds the picture's first macroblock, or at what may only
/// stand before one (a delimiter, a parameter set, supplemental information); the parameter sets are taken out of
/// the stream and announced once, delimiters and filler are dropped. Frame pictures only, which is what the encoders
/// are asked for. One thread at a time.
/// </summary>
/// <param name="receiver">Where the parameter sets and the frames go.</param>
public sealed class H264AnnexBSplitter(IH264Receiver receiver)
{
	/// <summary>The bytes not yet cut: from the first byte of the NAL unit in progress, once a start code was seen.</summary>
	private byte[] _pending = new byte[1 << 16];
	private int _length;
	/// <summary>How many of the pending bytes have been searched for a start code.</summary>
	private int _searched;
	private bool _inUnit;
	/// <summary>The frame in progress: its NAL units with their lengths.</summary>
	private byte[] _frame = new byte[1 << 16];
	private int _frameLength;
	private bool _frameHasPicture;
	private bool _frameIsKey;
	private byte[]? _sequence;
	private byte[]? _picture;
	private bool _announced;

	/// <summary>Frames handed to the receiver so far.</summary>
	public int Frames { get; private set; }

	/// <summary>Takes the stream's next bytes and hands on every frame they complete.</summary>
	/// <param name="bytes">The next piece of the stream, of any length.</param>
	/// <exception cref="InvalidDataException">The stream is not one an MP4 can hold (see <see cref="Complete"/>).</exception>
	public void Push(ReadOnlySpan<byte> bytes)
	{
		Grow(ref _pending, _length, _length + bytes.Length);
		bytes.CopyTo(_pending.AsSpan(_length));
		_length += bytes.Length;
		while (true)
		{
			// a start code may straddle two pieces: its first two bytes may have been searched already
			int from = Math.Max(0, _searched - 2);
			int at = _pending.AsSpan(from, _length - from).IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
			if (at < 0)
			{
				_searched = _length;
				return;
			}
			at += from;
			if (_inUnit)
			{
				Unit(_pending.AsSpan(0, TrimmedLength(at)));
			}
			// what follows the start code is the next unit, from the buffer's beginning
			int next = at + 3;
			_pending.AsSpan(next, _length - next).CopyTo(_pending);
			_length -= next;
			_searched = 0;
			_inUnit = true;
		}
	}

	/// <summary>The stream has ended: hands on the last frame.</summary>
	/// <exception cref="InvalidDataException">A picture came before the parameter sets, or the encoder changed them in mid-stream.</exception>
	public void Complete()
	{
		if (_inUnit)
		{
			Unit(_pending.AsSpan(0, TrimmedLength(_length)));
		}
		_inUnit = false;
		_length = 0;
		_searched = 0;
		if (_frameHasPicture)
		{
			Flush();
		}
		_frameLength = 0;
	}

	/// <summary>The length of the unit that ends at <paramref name="end"/>, without the zero bytes that belong to the start code behind it (a unit never ends in one).</summary>
	private int TrimmedLength(int end)
	{
		while (end > 0 && _pending[end - 1] == 0)
		{
			end--;
		}
		return end;
	}

	/// <summary>One NAL unit, header byte first: into the frame in progress, or the end of it.</summary>
	private void Unit(ReadOnlySpan<byte> unit)
	{
		if (unit.IsEmpty)
		{
			return;
		}
		int type = unit[0] & 0x1F;
		switch (type)
		{
			case 1:
			case 5:
				// a slice; first_mb_in_slice is the header's first value, and its code for 0 is a single 1 bit
				bool firstOfPicture = unit.Length > 1 && (unit[1] & 0x80) != 0;
				if (firstOfPicture && _frameHasPicture)
				{
					Flush();
				}
				Append(unit);
				_frameHasPicture = true;
				_frameIsKey |= type == 5;
				break;
			case 6:
				// supplemental information stands before its picture
				EndPicture();
				Append(unit);
				break;
			case 7:
				EndPicture();
				_sequence = Kept(_sequence, unit);
				break;
			case 8:
				EndPicture();
				_picture = Kept(_picture, unit);
				break;
			default:
				// a delimiter, the end of the sequence or the stream, filler: they end a picture and are not stored
				EndPicture();
				break;
		}
	}

	private void EndPicture()
	{
		if (_frameHasPicture)
		{
			Flush();
		}
	}

	/// <summary>The parameter set as first seen; an encoder repeats it before every key frame, and a different one is refused.</summary>
	private static byte[] Kept(byte[]? known, ReadOnlySpan<byte> unit)
	{
		if (known == null)
		{
			return unit.ToArray();
		}
		if (!known.AsSpan().SequenceEqual(unit))
		{
			throw new InvalidDataException("The H.264 encoder changed its parameter sets in mid-stream, which the one sample description of an MP4 cannot hold.");
		}
		return known;
	}

	private void Append(ReadOnlySpan<byte> unit)
	{
		Grow(ref _frame, _frameLength, _frameLength + 4 + unit.Length);
		BinaryPrimitives.WriteUInt32BigEndian(_frame.AsSpan(_frameLength), (uint)unit.Length);
		unit.CopyTo(_frame.AsSpan(_frameLength + 4));
		_frameLength += 4 + unit.Length;
	}

	private void Flush()
	{
		if (_sequence == null || _picture == null)
		{
			throw new InvalidDataException("The H.264 stream has a picture before its parameter sets.");
		}
		if (!_announced)
		{
			receiver.ParameterSets(_sequence, _picture);
			_announced = true;
		}
		receiver.Frame(_frame.AsSpan(0, _frameLength), _frameIsKey);
		Frames++;
		_frameLength = 0;
		_frameHasPicture = false;
		_frameIsKey = false;
	}

	/// <summary>Makes room for <paramref name="needed"/> bytes, keeping the first <paramref name="used"/>.</summary>
	private static void Grow(ref byte[] buffer, int used, int needed)
	{
		if (needed <= buffer.Length)
		{
			return;
		}
		var larger = new byte[Math.Max(needed, buffer.Length * 2)];
		buffer.AsSpan(0, used).CopyTo(larger);
		buffer = larger;
	}
}

/// <summary>
/// What an MP4's decoder configuration repeats of an H.264 sequence parameter set: the profile, its constraint flags
/// and the level, and for the profiles that say so the chroma format and the sample depths (the others are 4:2:0 at
/// 8 bits by definition).
/// </summary>
/// <param name="Profile">profile_idc: 66 Baseline, 77 Main, 100 High.</param>
/// <param name="Compatibility">The byte of constraint flags behind the profile.</param>
/// <param name="Level">level_idc: ten times the level, 40 for 4.0.</param>
/// <param name="ChromaFormat">chroma_format_idc: 1 is 4:2:0.</param>
/// <param name="LumaBits">Bits of a luma sample.</param>
/// <param name="ChromaBits">Bits of a chroma sample.</param>
public readonly record struct H264SequenceFacts(int Profile, int Compatibility, int Level, int ChromaFormat, int LumaBits, int ChromaBits)
{
	/// <summary>Whether the profile's parameter set names its chroma format and depths, the profiles from High on (7.3.2.1.1 of the standard).</summary>
	public bool NamesFormat => Profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135;

	/// <summary>Reads the facts from a sequence parameter set.</summary>
	/// <param name="sequence">The parameter set as one NAL unit, header byte first.</param>
	/// <exception cref="InvalidDataException">It is no sequence parameter set, or it ends too early.</exception>
	public static H264SequenceFacts Read(ReadOnlySpan<byte> sequence)
	{
		if (sequence.Length < 4 || (sequence[0] & 0x1F) != 7)
		{
			throw new InvalidDataException("Not an H.264 sequence parameter set.");
		}
		var facts = new H264SequenceFacts(sequence[1], sequence[2], sequence[3], 1, 8, 8);
		if (!facts.NamesFormat)
		{
			return facts;
		}
		// the payload behind the three bytes, without the 03 bytes that keep it free of start codes; the values
		// wanted lie in its first bytes
		Span<byte> payload = stackalloc byte[Math.Min(16, sequence.Length - 4)];
		int length = 0;
		int zeros = 0;
		for (int i = 4; i < sequence.Length && length < payload.Length; i++)
		{
			byte b = sequence[i];
			if (zeros >= 2 && b == 3)
			{
				zeros = 0;
				continue;
			}
			zeros = b == 0 ? zeros + 1 : 0;
			payload[length++] = b;
		}
		var bits = new Bits(payload[..length]);
		bits.Unsigned(); // seq_parameter_set_id
		int chroma = bits.Unsigned();
		if (chroma == 3)
		{
			bits.Bit(); // separate_colour_plane_flag
		}
		int luma = 8 + bits.Unsigned();
		int chromaBits = 8 + bits.Unsigned();
		return facts with { ChromaFormat = chroma, LumaBits = luma, ChromaBits = chromaBits };
	}

	/// <summary>Reads bits and the standard's unsigned Exp-Golomb codes, ue(v), from the front of a payload.</summary>
	private ref struct Bits(ReadOnlySpan<byte> bytes)
	{
		private readonly ReadOnlySpan<byte> _bytes = bytes;
		private int _position;

		public int Bit()
		{
			if (_position >= _bytes.Length * 8)
			{
				throw new InvalidDataException("The H.264 sequence parameter set ends too early.");
			}
			int bit = (_bytes[_position >> 3] >> (7 - (_position & 7))) & 1;
			_position++;
			return bit;
		}

		/// <summary>ue(v): n zero bits, a one, then n bits; the value is 2^n − 1 plus those bits.</summary>
		public int Unsigned()
		{
			int zeros = 0;
			while (Bit() == 0)
			{
				zeros++;
			}
			int value = 0;
			for (int i = 0; i < zeros; i++)
			{
				value = (value << 1) | Bit();
			}
			return (1 << zeros) - 1 + value;
		}
	}
}
