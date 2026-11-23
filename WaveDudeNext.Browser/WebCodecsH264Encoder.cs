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
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using NLog;
using WaveDudeNext.Export;

namespace WaveDudeNext.Browser;

/// <summary>
/// The browser's own H.264 encoder (WebCodecs) as the browser head's <see cref="IH264Encoder"/> (2026-11-21, the
/// user's choice; the design of 2026-11-15 had named it: every platform's own encoder behind the one interface, the
/// in-house container around its stream). Set as <see cref="ExportFormats.PlatformH264"/> by <c>Main</c> where the
/// browser has such an encoder; MP4 is then among the export formats, and nowhere else in this head.
/// <para>
/// The encoder is the page's (<c>h264-encoder.js</c>): a frame goes to it from the program's memory, where it is
/// held in place for the call, and what it has encoded is taken back into the program's memory with the next call,
/// so the receiver is called on the thread that drives the encoder, inside <see cref="Encode"/> and
/// <see cref="Finish"/>. The browser hands the stream over as an MP4 stores it: the parameter sets apart, in the
/// decoder's configuration that comes with the first frame, and each frame's NAL units behind their lengths. It is
/// asked for the High profile, then Main, then Constrained Baseline, the first it has for the picture's size (a
/// browser's software encoder is Baseline alone, a graphics card's does High), at the lowest level that holds the
/// size, the rate and the bitrate, which is the author's rule's (<see cref="H264Settings.Bitrate"/>); a key frame
/// a second. A browser's encoders give no B-frames; one that did would be refused in words, since the file has no
/// place for another order than the frames'.
/// </para>
/// <para>
/// Threads: one at a time drives an encoder, any but the scripts' own (<see cref="ScriptThread"/>); every call
/// waits for the page, 0.3 to 0.6 ms of hand-over each, two a frame.
/// </para>
/// </summary>
internal sealed partial class WebCodecsH264Encoder : IH264Encoder
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(WebCodecsH264Encoder).FullName!);

	/// <summary>The length of a record's header in what the page hands back: the bytes' length, the kind and the frame's number (<c>h264-encoder.js</c>).</summary>
	private const int RecordHeader = 12;

	/// <summary>The record kinds of the page's script.</summary>
	private const int KindConfiguration = 2;

	/// <summary>The profiles asked for, the best first: the name, profile_idc and the constraint flags' byte.</summary>
	private static readonly (string Name, int Profile, int Constraints)[] Profiles =
	[
		("High", 0x64, 0x00),
		("Main", 0x4D, 0x00),
		("Constrained Baseline", 0x42, 0xE0),
	];

	/// <summary>
	/// The standard's levels (table A-1): level_idc, the macroblocks a second, the macroblocks a frame, and the
	/// bitrate in 1000 bits a second for the Baseline and Main profiles (High may take a quarter more).
	/// </summary>
	private static readonly (int Level, int MacroblocksPerSecond, int MacroblocksPerFrame, int Kilobits)[] Levels =
	[
		(10, 1485, 99, 64), (11, 3000, 396, 192), (12, 6000, 396, 384), (13, 11880, 396, 768),
		(20, 11880, 396, 2000), (21, 19800, 792, 4000), (22, 20250, 1620, 4000),
		(30, 40500, 1620, 10000), (31, 108000, 3600, 14000), (32, 216000, 5120, 20000),
		(40, 245760, 8192, 20000), (41, 245760, 8192, 50000), (42, 522240, 8704, 50000),
		(50, 589824, 22080, 135000), (51, 983040, 36864, 240000), (52, 2073600, 36864, 240000),
		(60, 4177920, 139264, 240000), (61, 8355840, 139264, 480000), (62, 16711680, 139264, 800000),
	];

	private static Task? _module;

	private readonly int _number;
	private readonly IH264Receiver _receiver;
	private readonly int _keyDistance;
	private byte[] _output = new byte[1 << 18];
	private byte[] _units = new byte[1 << 16];
	private byte[]? _sequence;
	private byte[]? _picture;
	private int _lengthSize = 4;
	private int _given;
	private int _delivered;
	private int _lastIndex = -1;
	private bool _finished;

	private WebCodecsH264Encoder(int number, H264Settings settings, IH264Receiver receiver, string description)
	{
		_number = number;
		_receiver = receiver;
		_keyDistance = Math.Max(1, settings.FramesPerSecond);
		Description = description;
	}

	/// <inheritdoc/>
	public string Description { get; }

	/// <summary>
	/// Asks the browser, once at the program's start, whether it encodes H.264 for a page: a Full HD picture at 30
	/// frames a second, the usual export. On the scripts' thread (<c>Main</c>'s).
	/// </summary>
	/// <returns>The head's encoder for <see cref="ExportFormats.PlatformH264"/>, or null where the browser has none.</returns>
	public static async Task<PlatformH264Encoder?> FindAsync()
	{
		await Module();
		return await ChooseAsync(new H264Settings(1920, 1080, 30, 70)) is var (_, profile)
			? new PlatformH264Encoder("WebCodecs (" + profile + " profile)", Open)
			: null;
	}

	/// <summary>Makes the browser's encoder for the settings; the form <see cref="PlatformH264Encoder.Open"/> takes.</summary>
	/// <param name="settings">The picture's size, the frame rate and the quality.</param>
	/// <param name="receiver">Where the encoded frames go; called on the driving thread, inside <see cref="Encode"/> and <see cref="Finish"/>.</param>
	/// <exception cref="NotSupportedException">The browser's encoder does not take a picture of this size and rate.</exception>
	/// <exception cref="IOException">The encoder could not be made.</exception>
	public static IH264Encoder Open(H264Settings settings, IH264Receiver receiver)
	{
		(int number, string codec, string profile) = Call(async () =>
		{
			await Module();
			return await ChooseAsync(settings) is var (chosen, name)
				? (JsOpen(chosen, settings.Width, settings.Height, settings.FramesPerSecond, settings.Bitrate), chosen, name)
				: (0, "", "");
		});
		if (number == 0)
		{
			throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
				$"The browser's H.264 encoder does not take a picture of {settings.Width} x {settings.Height} at {settings.FramesPerSecond} frames a second."));
		}
		string description = "WebCodecs (" + profile + " profile, " + codec + ")";
		Logger.Debug("H.264 through the browser's encoder: {Description}, {Bitrate} bit/s", description, settings.Bitrate);
		return new WebCodecsH264Encoder(number, settings, receiver, description);
	}

	/// <summary>
	/// The codec string for a profile and the settings, as a browser is asked (<c>avc1.</c>, then the profile, the
	/// constraint flags and the level as two hexadecimal digits each): the lowest level that holds the picture's
	/// size, its macroblocks a second and the bitrate; null where no level does.
	/// </summary>
	internal static string? CodecOf(int profile, int constraints, H264Settings settings)
	{
		int across = (settings.Width + 15) / 16;
		int down = (settings.Height + 15) / 16;
		long frame = (long)across * down;
		long second = frame * settings.FramesPerSecond;
		foreach ((int level, int perSecond, int perFrame, int kilobits) in Levels)
		{
			// a picture may also be no wider or higher than the square root of eight times the level's frame size
			double side = Math.Sqrt(8.0 * perFrame);
			long bits = kilobits * (profile == 0x64 ? 1250L : 1000L);
			if (frame <= perFrame && second <= perSecond && across <= side && down <= side && settings.Bitrate <= bits)
			{
				return string.Create(CultureInfo.InvariantCulture, $"avc1.{profile:X2}{constraints:X2}{level:X2}");
			}
		}
		return null;
	}

	/// <summary>The first profile the browser encodes the settings in, with its codec string; null when it has none. On the scripts' thread.</summary>
	private static async Task<(string Codec, string Profile)?> ChooseAsync(H264Settings settings)
	{
		foreach ((string name, int profile, int constraints) in Profiles)
		{
			if (CodecOf(profile, constraints, settings) is { } codec
				&& await JsSupported(codec, settings.Width, settings.Height, settings.FramesPerSecond, settings.Bitrate))
			{
				return (codec, name);
			}
		}
		return null;
	}

	/// <inheritdoc/>
	public unsafe void Encode(ReadOnlySpan<byte> nv12)
	{
		if (_finished)
		{
			throw new InvalidOperationException("The encoder is finished.");
		}
		int index = _given;
		bool key = index % _keyDistance == 0;
		int waiting;
		fixed (byte* bytes = nv12)
		{
			nint address = (nint)bytes;
			int length = nv12.Length;
			waiting = Call(() => JsEncode(_number, address, length, index, key));
		}
		_given++;
		Deliver(waiting);
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
			Deliver(Call(() => JsFinish(_number)));
		}
		finally
		{
			try
			{
				Call(() =>
				{
					JsClose(_number);
					return Task.FromResult(0);
				});
			}
			catch (IOException ex)
			{
				Logger.Debug(ex, "The browser's H.264 encoder could not be given back");
			}
			Logger.Debug("The browser's H.264 encoder made {Delivered} frames of the {Given} given", _delivered, _given);
		}
	}

	/// <summary>Takes what the page holds of encoded frames, <paramref name="waiting"/> bytes of records at the least, and hands it to the receiver.</summary>
	private unsafe void Deliver(int waiting)
	{
		while (waiting > 0)
		{
			if (_output.Length < waiting)
			{
				_output = new byte[Math.Max(waiting, _output.Length * 2)];
			}
			int taken;
			fixed (byte* bytes = _output)
			{
				nint address = (nint)bytes;
				int capacity = _output.Length;
				taken = Call(() => Task.FromResult(JsTake(_number, address, capacity)));
			}
			if (taken < 0)
			{
				// a record that came since and is larger than the room: asked again with room for it
				waiting = -taken;
				continue;
			}
			if (taken == 0)
			{
				return;
			}
			ReadOnlySpan<byte> records = _output.AsSpan(0, taken);
			while (records.Length >= RecordHeader)
			{
				int size = BinaryPrimitives.ReadInt32LittleEndian(records);
				int kind = BinaryPrimitives.ReadInt32LittleEndian(records[4..]);
				int index = BinaryPrimitives.ReadInt32LittleEndian(records[8..]);
				ReadOnlySpan<byte> bytes = records.Slice(RecordHeader, size);
				if (kind == KindConfiguration)
				{
					Configuration(bytes);
				}
				else
				{
					Frame(bytes, index);
				}
				records = records[(RecordHeader + size)..];
			}
			waiting -= taken;
		}
	}

	/// <summary>
	/// The decoder's configuration as the browser gives it with the first frame: the avcC record of an MP4 (ISO
	/// 14496-15, 5.3.3.1), from which the receiver gets the two parameter sets and this the size of the lengths in
	/// front of a frame's NAL units.
	/// </summary>
	/// <exception cref="IOException">The record is not one, or names other parameter sets than the first did.</exception>
	private void Configuration(ReadOnlySpan<byte> record)
	{
		byte[]? sequence = null;
		byte[]? picture = null;
		bool whole = record.Length >= 7 && record[0] == 1;
		int at = 5;
		if (whole)
		{
			whole = Sets(record, ref at, record[at++] & 0x1F, ref sequence) && at < record.Length && Sets(record, ref at, record[at++], ref picture);
		}
		if (!whole || sequence == null || picture == null || sequence.Length < 4 || picture.Length < 1)
		{
			throw new IOException("The browser's H.264 encoder gave a decoder configuration that names no parameter sets.");
		}
		if (_sequence == null || _picture == null)
		{
			_lengthSize = (record[4] & 3) + 1;
			_sequence = sequence;
			_picture = picture;
			// a sequence parameter set begins with its header byte, the profile, the constraint flags and the level
			Logger.Debug("The browser's H.264 stream: profile {Profile}, level {Level}, lengths of {LengthSize} bytes", sequence[1], sequence[3] / 10.0, _lengthSize);
			_receiver.ParameterSets(sequence, picture);
		}
		else if (!_sequence.AsSpan().SequenceEqual(sequence) || !_picture.AsSpan().SequenceEqual(picture))
		{
			throw new IOException("The browser's H.264 encoder changed its parameter sets in mid-stream, which the one sample description of an MP4 cannot hold.");
		}
	}

	/// <summary>Reads <paramref name="count"/> parameter sets, each behind its two-byte length, and keeps the first; false when the record ends too early.</summary>
	private static bool Sets(ReadOnlySpan<byte> record, ref int at, int count, ref byte[]? first)
	{
		for (int i = 0; i < count; i++)
		{
			if (at + 2 > record.Length)
			{
				return false;
			}
			int length = BinaryPrimitives.ReadUInt16BigEndian(record[at..]);
			at += 2;
			if (at + length > record.Length)
			{
				return false;
			}
			first ??= record.Slice(at, length).ToArray();
			at += length;
		}
		return true;
	}

	/// <summary>
	/// One encoded frame as the browser gives it: its NAL units behind their lengths. Handed on with four-byte
	/// lengths and with nothing but the picture's slices and the supplemental information before them; a parameter
	/// set, a delimiter or filler among them is left out, as the receiver wants it. A key frame is one with a slice
	/// a decoder can start at, whatever the browser called it.
	/// </summary>
	/// <exception cref="IOException">A frame before the configuration, out of its order, or cut short.</exception>
	private void Frame(ReadOnlySpan<byte> frame, int index)
	{
		if (_sequence == null)
		{
			throw new IOException("The browser's H.264 encoder gave a picture before its parameter sets.");
		}
		if (index <= _lastIndex)
		{
			throw new IOException(string.Create(CultureInfo.InvariantCulture,
				$"The browser's H.264 encoder gave frame {index} after frame {_lastIndex}: it reorders the frames, which this file cannot hold."));
		}
		_lastIndex = index;
		int length = 0;
		bool picture = false;
		bool key = false;
		while (frame.Length >= _lengthSize)
		{
			int unit = 0;
			for (int i = 0; i < _lengthSize; i++)
			{
				unit = (unit << 8) | frame[i];
			}
			frame = frame[_lengthSize..];
			if (unit < 0 || unit > frame.Length)
			{
				throw new IOException("The browser's H.264 encoder gave a frame whose last NAL unit is cut short.");
			}
			int type = unit > 0 ? frame[0] & 0x1F : 0;
			if (type is 1 or 5 or 6)
			{
				if (_units.Length < length + 4 + unit)
				{
					Array.Resize(ref _units, Math.Max(length + 4 + unit, _units.Length * 2));
				}
				BinaryPrimitives.WriteUInt32BigEndian(_units.AsSpan(length), (uint)unit);
				frame[..unit].CopyTo(_units.AsSpan(length + 4));
				length += 4 + unit;
				picture |= type != 6;
				key |= type == 5;
			}
			frame = frame[unit..];
		}
		if (!picture)
		{
			Logger.Debug("The browser's H.264 encoder gave a frame {Index} without a picture; left out", index);
			return;
		}
		_receiver.Frame(_units.AsSpan(0, length), key);
		_delivered++;
	}

	/// <summary>The page's module, loaded once; on the scripts' thread.</summary>
	private static Task Module() => _module ??= JSHost.ImportAsync("h264-encoder", "../h264-encoder.js");

	/// <summary>A call to the page, waited for; what fails there is an <see cref="IOException"/> here, as an encoder's failure is.</summary>
	private static T Call<T>(Func<Task<T>> call)
	{
		try
		{
			return ScriptThread.Await(call);
		}
		catch (InvalidOperationException ex) when (ex.InnerException is { } inner)
		{
			string words = inner.Message.StartsWith("Error: ", StringComparison.Ordinal) ? inner.Message[7..] : inner.Message;
			throw new IOException("The browser's H.264 encoder: " + words, inner);
		}
	}

	[JSImport("supported", "h264-encoder")]
	private static partial Task<bool> JsSupported(string codec, int width, int height, int framesPerSecond, int bitrate);

	[JSImport("open", "h264-encoder")]
	private static partial int JsOpen(string codec, int width, int height, int framesPerSecond, int bitrate);

	[JSImport("encode", "h264-encoder")]
	private static partial Task<int> JsEncode(int encoder, nint address, int length, int index, bool key);

	[JSImport("take", "h264-encoder")]
	private static partial int JsTake(int encoder, nint address, int capacity);

	[JSImport("finish", "h264-encoder")]
	private static partial Task<int> JsFinish(int encoder);

	[JSImport("close", "h264-encoder")]
	private static partial void JsClose(int encoder);
}
