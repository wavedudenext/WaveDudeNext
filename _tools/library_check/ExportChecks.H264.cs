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
using System.Text;
using WaveDudeNext.Export;

namespace LibraryCheck;

// The MP4 of the platforms whose encoder hands back encoded frames (2026-11-15): the stream splitter and the
// container on streams made by hand, where every byte is known; then the system's ffmpeg as the encoder, when one
// with an H.264 encoder is on the path, its file read back by ffmpeg's own demuxer and decoder. Nothing here is
// Linux's alone, so the Linux path is proven on whatever machine runs this.
internal static partial class ExportChecks
{
	/// <summary>Keeps what an encoder or the splitter delivers.</summary>
	private sealed class H264Record : IH264Receiver
	{
		public List<byte[]> Sets { get; } = [];

		public List<(byte[] Units, bool Key)> Frames { get; } = [];

		public void ParameterSets(ReadOnlySpan<byte> sequence, ReadOnlySpan<byte> picture)
		{
			Sets.Add(sequence.ToArray());
			Sets.Add(picture.ToArray());
		}

		public void Frame(ReadOnlySpan<byte> units, bool keyFrame) => Frames.Add((units.ToArray(), keyFrame));
	}

	/// <summary>An encoder that hands on frames made beforehand, one per frame given: the container and the sink without any real encoder.</summary>
	private sealed class CannedEncoder(IH264Receiver receiver, byte[] sequence, byte[] picture, IReadOnlyList<(byte[] Units, bool Key)> frames) : IH264Encoder
	{
		private int _given;

		public List<byte[]> Taken { get; } = [];

		public bool Finished { get; private set; }

		public string Description => "canned";

		public void Encode(ReadOnlySpan<byte> nv12)
		{
			Taken.Add(nv12.ToArray());
			if (_given == 0)
			{
				receiver.ParameterSets(sequence, picture);
			}
			(byte[] units, bool key) = frames[_given++ % frames.Count];
			receiver.Frame(units, key);
		}

		public void Finish() => Finished = true;
	}

	private static void CheckH264(Action<bool, string> check, string folder, int[][] frames, int w, int h)
	{
		Console.WriteLine("--- export: H.264 through a platform's encoder, the in-house MP4 container");

		// ---- the splitter: a stream made by hand, with both forms of the start code, zero bytes before one, a picture
		// of two slices, supplemental information, a delimiter, and the parameter sets again before the second key frame
		byte[] sequence = [0x67, 100, 0x00, 30, 0xAC, 0xD9, 0x41, 0x41];
		byte[] picture = [0x68, 0xEB, 0xE3, 0xCB];
		byte[] information = [0x06, 0x05, 0x04, 1, 2, 3, 4, 0x80];
		byte[] keySlice = [0x65, 0x88, 0x84, 0x21, 0xA0];
		byte[] slice = [0x41, 0x9A, 0x24, 0x6C];
		byte[] secondSlice = [0x41, 0x1A, 0x55]; // the same picture's: its first macroblock is not the picture's
		byte[] thirdPicture = [0x41, 0x9A, 0x02];
		byte[] delimiter = [0x09, 0xF0];
		using var made = new MemoryStream();
		void Long(byte[] unit) => made.Write([0, 0, 0, 1, .. unit]);
		void Short(byte[] unit) => made.Write([0, 0, 1, .. unit]);
		Long(sequence);
		Long(picture);
		Short(information);
		Short(keySlice);
		Long(slice);
		Short(secondSlice);
		made.Write([0, 0]); // trailing zero bytes, which belong to no unit
		Long(delimiter);
		Long(thirdPicture);
		Long(sequence);
		Long(picture);
		Short(keySlice);
		byte[] stream = made.ToArray();
		byte[] Lengths(params byte[][] units)
		{
			using var joined = new MemoryStream();
			Span<byte> length = stackalloc byte[4];
			foreach (byte[] unit in units)
			{
				BinaryPrimitives.WriteUInt32BigEndian(length, (uint)unit.Length);
				joined.Write(length);
				joined.Write(unit);
			}
			return joined.ToArray();
		}
		(byte[] Units, bool Key)[] expected = [(Lengths(information, keySlice), true), (Lengths(slice, secondSlice), false), (Lengths(thirdPicture), false), (Lengths(keySlice), true)];
		bool SplitsRight(int piece, out string got)
		{
			var record = new H264Record();
			var splitter = new H264AnnexBSplitter(record);
			for (int at = 0; at < stream.Length; at += piece)
			{
				splitter.Push(stream.AsSpan(at, Math.Min(piece, stream.Length - at)));
			}
			bool lastHeldBack = record.Frames.Count == expected.Length - 1;
			splitter.Complete();
			got = record.Frames.Count + " frames, keys " + string.Join("", record.Frames.Select(f => f.Key ? "K" : "-")) + ", " + record.Sets.Count + " sets";
			return lastHeldBack && splitter.Frames == expected.Length && record.Frames.Count == expected.Length
				&& record.Sets.Count == 2 && record.Sets[0].SequenceEqual(sequence) && record.Sets[1].SequenceEqual(picture)
				&& record.Frames.Select((f, i) => f.Key == expected[i].Key && f.Units.SequenceEqual(expected[i].Units)).All(same => same);
		}
		foreach (int piece in new[] { stream.Length, 1, 3, 7 })
		{
			check(SplitsRight(piece, out string got), "h264 splitter: a stream in pieces of " + piece + " bytes gives 4 frames (a two-slice picture one of them), the key frames marked, the parameter sets once and out of the frames, the last frame at the end (" + got + ")");
		}
		bool refused = false;
		try
		{
			var splitter = new H264AnnexBSplitter(new H264Record());
			splitter.Push([0, 0, 1, .. sequence, 0, 0, 1, .. picture, 0, 0, 1, .. keySlice, 0, 0, 1, 0x67, 66, 0, 30, 0, 0, 1, .. keySlice]);
			splitter.Complete();
		}
		catch (InvalidDataException)
		{
			refused = true;
		}
		check(refused, "h264 splitter: parameter sets that change in mid-stream are refused in words");
		refused = false;
		try
		{
			var splitter = new H264AnnexBSplitter(new H264Record());
			splitter.Push([0, 0, 1, .. keySlice]);
			splitter.Complete();
		}
		catch (InvalidDataException)
		{
			refused = true;
		}
		check(refused, "h264 splitter: a picture before its parameter sets is refused in words");

		// ---- the sequence parameter set's facts: a High one names its chroma format and depths, a Baseline one does not
		H264SequenceFacts high = H264SequenceFacts.Read(sequence);
		H264SequenceFacts baseline = H264SequenceFacts.Read([0x67, 66, 0xC0, 30, 0xDA, 0x02]);
		check(high == new H264SequenceFacts(100, 0, 30, 1, 8, 8) && baseline == new H264SequenceFacts(66, 0xC0, 30, 1, 8, 8),
			"h264 parameter set: profile, flags and level read, 4:2:0 at 8 bits for High (named) and Baseline (by definition): " + high + " / " + baseline);
		// 10 bits in 4:2:2: the set's id 0 is the bit '1', the chroma format 2 is '011', both depths' 2 are '011' each
		H264SequenceFacts deep = H264SequenceFacts.Read([0x67, 122, 0, 40, 0b1011_0110, 0b1100_0000]);
		check(deep == new H264SequenceFacts(122, 0, 40, 2, 10, 10), "h264 parameter set: High 4:2:2 at 10 bits reads so: " + deep);

		// ---- the container: the frames of the hand-made stream, once or a hundred times over; the index at the front
		// when the planned count held it, at the end when none was planned or far more frames came than planned
		foreach ((int planned, int rounds) in new[] { (8, 1), (0, 1), (1, 100) })
		{
			int count = expected.Length * rounds;
			var bytes = new MemoryStream();
			var container = new Mp4ContainerWriter(bytes, 70, 46, 30, planned);
			container.ParameterSets(sequence, picture);
			for (int i = 0; i < count; i++)
			{
				container.Frame(expected[i % expected.Length].Units, expected[i % expected.Length].Key);
			}
			container.Close();
			container.Close();
			Mp4Walk walk = WalkMp4(bytes.ToArray());
			bool front = planned >= count;
			string what = "mp4 container, " + count + " frames with " + planned + " planned: ";
			check(walk.Problem == null && container.MoovFirst == front && walk.MoovBeforeMdat == front && walk.EndsAtFileEnd && container.FrameCount == count,
				what + "whole boxes to the file's end, the index " + (front ? "at the front, over its reservation" : "at the end") + (walk.Problem != null ? " (" + walk.Problem + ")" : ""));
			int[] keys = [.. Enumerable.Range(0, count).Where(i => expected[i % expected.Length].Key).Select(i => i + 1)];
			check(walk.Width == 70 && walk.Height == 46 && walk.Timescale == 30 && walk.Duration == count && walk.SampleEntry == "avc1" && walk.KeyFrames.SequenceEqual(keys)
				&& walk.Samples.Count == count && walk.Samples.Select((s, i) => s.SequenceEqual(expected[i % expected.Length].Units)).All(same => same),
				what + "70 x 46, every sample one thirtieth of a second, the bytes at their offsets the frames given, the " + keys.Length + " key frames listed (" + walk.Samples.Count + " samples, " + walk.KeyFrames.Count + " keys)");
			byte[] configuration = Mp4ContainerWriter.DecoderConfiguration(sequence, picture);
			check(walk.Configuration != null && walk.Configuration.SequenceEqual(configuration) && configuration is [1, 100, 0, 30, 0xFF, 0xE1, 0, 8, .., 0xFD, 0xF8, 0xF8, 0]
				&& configuration.AsSpan(8, 8).SequenceEqual(sequence) && configuration.AsSpan(16, 3).SequenceEqual((byte[])[1, 0, 4]) && configuration.AsSpan(19, 4).SequenceEqual(picture),
				what + "the decoder configuration holds profile, flags, level, 4-byte lengths, both parameter sets and High's format");
		}
		byte[] plain = Mp4ContainerWriter.DecoderConfiguration([0x67, 66, 0xC0, 30, 0xDA, 0x02], picture);
		check(plain.Length == 6 + 2 + 6 + 3 + 4 && plain.AsSpan(^4).SequenceEqual(picture), "mp4 container: a Baseline stream's decoder configuration ends with its picture parameter set");
		bool noSeek = false;
		try
		{
			_ = new Mp4ContainerWriter(new System.IO.Compression.DeflateStream(new MemoryStream(), System.IO.Compression.CompressionLevel.Fastest), 64, 48, 30);
		}
		catch (ArgumentException)
		{
			noSeek = true;
		}
		check(noSeek, "mp4 container: a stream that cannot seek is refused in words");

		// ---- the sink with a stand-in encoder: an odd size is padded to an even one, the frames go in as NV12 of
		// the planes the Windows writer uses, the encoder is finished before the file is closed
		CannedEncoder? canned = null;
		H264Settings? asked = null;
		var sinkBytes = new MemoryStream();
		using (var sink = new EncoderMp4Writer(sinkBytes, "memory", w, h, 30, 80, frames.Length, (settings, receiver) =>
		{
			asked = settings;
			return canned = new CannedEncoder(receiver, sequence, picture, expected);
		}))
		{
			foreach (int[] frame in frames)
			{
				sink.Add(frame);
			}
			check(sink.FrameCount == frames.Length && sink.Width == w && sink.Height == h && sink.Encoder == "canned", "mp4 sink: counts the frames it took and names its encoder");
		}
		int ew = (w + 1) & ~1;
		int eh = (h + 1) & ~1;
		var planes = new YuvPlanes(ew, eh, studioRange: true, subsampled: true);
		var padded = new int[ew * eh];
		for (int y = 0; y < eh; y++)
		{
			for (int x = 0; x < ew; x++)
			{
				padded[y * ew + x] = frames[2][Math.Min(y, h - 1) * w + Math.Min(x, w - 1)];
			}
		}
		planes.Convert(padded);
		var nv12 = new byte[ew * eh * 3 / 2];
		planes.CopyNv12(nv12);
		Mp4Walk sinkWalk = WalkMp4(sinkBytes.ToArray());
		check(asked == new H264Settings(ew, eh, 30, 80) && canned != null && canned.Finished && canned.Taken.Count == frames.Length && canned.Taken[2].SequenceEqual(nv12),
			"mp4 sink: asks the encoder for the even size " + ew + " x " + eh + " of " + w + " x " + h + ", gives it each frame as NV12 with the last row and column repeated, and finishes it");
		check(sinkWalk.Problem == null && sinkWalk.MoovBeforeMdat && sinkWalk.Samples.Count == frames.Length && sinkWalk.Width == ew && sinkWalk.Height == eh,
			"mp4 sink: the file holds the encoder's " + frames.Length + " frames at " + ew + " x " + eh + ", the index at the front" + (sinkWalk.Problem != null ? " (" + sinkWalk.Problem + ")" : ""));
		// 0.01 × 50^0.7 = 0.1546 bits a pixel at quality 70, on 62,208,000 pixels a second
		check(H264Settings.BitrateFor(1920, 1080, 30, 70) is > 9_600_000 and < 9_640_000 && new H264Settings(1920, 1080, 30, 70).Bitrate == H264Settings.BitrateFor(1920, 1080, 30, 70)
			&& H264Settings.BitrateFor(16, 16, 1, 1) == 300_000 && H264Settings.BitrateFor(8192, 4320, 120, 100) == 200_000_000,
			"h264 settings: the author's bitrate rule, " + H264Settings.BitrateFor(1920, 1080, 30, 70) + " bit/s for full HD at 30 fps and quality 70, held between 300 kbit/s and 200 Mbit/s");

		// ---- ffmpeg as the encoder: what it is asked, and which of its encoders is taken
		const string listing = " V..... = Video\n ------\n V..... libx264              libx264 H.264 (codec h264)\n V..... libopenh264          OpenH264 H.264 (codec h264)\n A..... libx264              no audio encoder is called so\n V..... h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)\n";
		const string fedora = " V..... libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)\r\n V..... h264_vaapi           H.264/AVC (VAAPI) (codec h264)\r\n";
		check(FfmpegH264Encoder.ChooseEncoder(listing) == "libx264" && FfmpegH264Encoder.ChooseEncoder(fedora) == "libopenh264" && FfmpegH264Encoder.ChooseEncoder(" V..... mpeg4  MPEG-4 part 2\n A..... libx264 x\n") == null,
			"ffmpeg encoder: x264 is taken where the list of encoders has it, OpenH264 where it has that alone, none from a list without either");
		check(FfmpegH264Encoder.VersionOf("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers\n  built with gcc 13") == "6.1.1-3ubuntu5" && FfmpegH264Encoder.VersionOf("something else") == "",
			"ffmpeg encoder: the version is the word behind 'ffmpeg version' in the banner");
		check(new FfmpegProbe(false, "", null).Obstacle == ExportObstacle.FfmpegMissing && new FfmpegProbe(true, "7.0", null).Obstacle == ExportObstacle.FfmpegWithoutH264 && new FfmpegProbe(true, "7.0", "libx264").Obstacle == null
			&& new FfmpegProbe(true, "7.0", "libx264").Description == "ffmpeg 7.0 (libx264)",
			"ffmpeg encoder: no ffmpeg and an ffmpeg without an H.264 encoder are two obstacles the application can name, one with an encoder none");
		string[] x264 = FfmpegH264Encoder.Arguments(new H264Settings(1920, 1080, 30, 70), "libx264");
		string[] openH264 = FfmpegH264Encoder.Arguments(new H264Settings(1920, 1080, 30, 70), "libopenh264");
		string line = string.Join(' ', x264);
		check(line.Contains("-f rawvideo -pix_fmt nv12 -video_size 1920x1080 -framerate 30 ") && line.Contains(" -i pipe:0 ") && line.Contains("-c:v libx264 ") && line.Contains("-crf 20 ") && line.Contains("-bf 0 ") && line.Contains("-g 30 ") && line.EndsWith("-f h264 pipe:1", StringComparison.Ordinal)
			&& string.Join(' ', openH264).Contains("-c:v libopenh264 -b:v " + H264Settings.BitrateFor(1920, 1080, 30, 70).ToString(CultureInfo.InvariantCulture) + " -g 30 "),
			"ffmpeg encoder: raw NV12 in from the pipe, raw H.264 out to the pipe, a key frame a second; x264 at a constant quality without B-frames, OpenH264 at the author's bitrate");
		check(ExportFormats.ObstacleTo(ExportFormat.Png) == null && ExportFormats.ObstacleTo(ExportFormat.Mov) == null && (ExportFormats.ObstacleTo(ExportFormat.Mp4) == null) == ExportFormats.IsAvailable(ExportFormat.Mp4),
			"formats: only an MP4 can lack something here, and it is available exactly when nothing is in its way (" + (ExportFormats.ObstacleTo(ExportFormat.Mp4)?.ToString() ?? "nothing") + ")");
		// ---- a head's own encoder (the browser head's): it takes the place of the library's ways while it is set
		CannedEncoder? headEncoder = null;
		string headPath = Path.Combine(folder, "head-encoder.mp4");
		ExportFormats.PlatformH264 = new PlatformH264Encoder("a head's stand-in", (_, receiver) => headEncoder = new CannedEncoder(receiver, sequence, picture, expected));
		try
		{
			bool free = ExportFormats.ObstacleTo(ExportFormat.Mp4) == null;
			string named;
			using (IFrameSink sink = ExportFormats.Open(ExportFormat.Mp4, headPath, w, h, 30, 80, frames.Length))
			{
				foreach (int[] frame in frames)
				{
					sink.Add(frame);
				}
				named = (sink as EncoderMp4Writer)?.Encoder ?? "";
			}
			Mp4Walk headWalk = WalkMp4(File.ReadAllBytes(headPath));
			check(free && named == "canned" && headEncoder is { Finished: true } && headEncoder.Taken.Count == frames.Length && headWalk.Problem == null && headWalk.Samples.Count == frames.Length,
				"formats: a head's own H.264 encoder, once set, is what an MP4 is written through, with the in-house container around its " + frames.Length + " frames, and nothing is in the format's way" + (headWalk.Problem != null ? " (" + headWalk.Problem + ")" : ""));
		}
		finally
		{
			ExportFormats.PlatformH264 = null;
		}
		check(FfmpegH264Encoder.Crf(70) == 20 && FfmpegH264Encoder.Crf(100) == 10 && FfmpegH264Encoder.Crf(1) == 43 && Enumerable.Range(1, 99).All(q => FfmpegH264Encoder.Crf(q + 1) <= FfmpegH264Encoder.Crf(q)),
			"ffmpeg encoder: x264's rate factor is 20 at quality 70, 10 at 100, 43 at 1, never rising with the quality");

		// ---- the real thing, when this machine has it: ffmpeg encodes, the in-house container holds the stream, and
		// ffmpeg's own demuxer and decoder read the file back
		FfmpegProbe probe = FfmpegH264Encoder.Probe();
		if (Ffmpeg == null || probe.Obstacle != null)
		{
			Console.WriteLine("  skip h264 through ffmpeg: " + (probe.Found ? probe.Description + " has no H.264 encoder" : "no ffmpeg on the path"));
			return;
		}
		Console.WriteLine("  h264 through " + probe.Description);
		try
		{
			// 45 frames of one picture with a small block that moves a pixel a frame: nothing an encoder takes for a
			// cut, so the key frames are the first and, a second later, the thirty-first
			int[][] many = [.. Enumerable.Range(0, 45).Select(i =>
			{
				int[] frame = TestFrame(w, h, 2);
				for (int y = 30; y < 34; y++)
				{
					frame.AsSpan(y * w + i, 4).Fill(unchecked((int)0xFFFFFFFF));
				}
				return frame;
			})];
			string path = Path.Combine(folder, "encoder.mp4");
			using (IFrameSink sink = EncoderMp4Writer.Create(path, w, h, 30, 80, many.Length, FfmpegH264Encoder.Open))
			{
				foreach (int[] frame in many)
				{
					sink.Add(frame);
				}
			}
			Mp4Walk walk = WalkMp4(File.ReadAllBytes(path));
			check(walk.Problem == null && walk.MoovBeforeMdat && walk.Samples.Count == 45 && walk.Duration == 45 && walk.Width == ew && walk.Height == eh && walk.KeyFrames.SequenceEqual([1, 31]),
				"mp4 through ffmpeg: 45 frames in the container, the index at the front, key frames at 1 and 31 (" + walk.Samples.Count + " samples, keys " + string.Join(",", walk.KeyFrames) + ")" + (walk.Problem != null ? " (" + walk.Problem + ")" : ""));
			bool unitsWhole = walk.Samples.All(sample =>
			{
				int at = 0;
				while (at + 4 <= sample.Length)
				{
					int length = BinaryPrimitives.ReadInt32BigEndian(sample.AsSpan(at));
					int type = at + 4 < sample.Length ? sample[at + 4] & 0x1F : -1;
					if (length <= 0 || type is 7 or 8 or 9)
					{
						return false;
					}
					at += 4 + length;
				}
				return at == sample.Length;
			});
			H264SequenceFacts? facts = walk.Configuration is { Length: > 8 } c ? H264SequenceFacts.Read(c.AsSpan(8, (c[6] << 8) | c[7])) : null;
			check(unitsWhole && facts is { ChromaFormat: 1, LumaBits: 8 }, "mp4 through ffmpeg: every sample is whole NAL units behind their lengths, no parameter set or delimiter among them; the stream is " + (facts?.ToString() ?? "unread"));
			byte[]? rgb = Decode(path, ew, eh, out int count);
			double first = rgb == null || count < 1 ? 0 : PsnrPadded(rgb.AsSpan(0, ew * eh * 3), many[0], w, h, ew);
			double last = rgb == null || count < 45 ? 0 : PsnrPadded(rgb.AsSpan(44 * ew * eh * 3, ew * eh * 3), many[44], w, h, ew);
			// the hard edges of the test picture cost a 4:2:0 format about 33 dB whatever encodes it (the Media Foundation's file reads the same)
			check(rgb != null && count == 45 && first > 28 && last > 28,
				"mp4 through ffmpeg: ffmpeg reads the file back as 45 frames of " + ew + " x " + eh + ", the first at " + first.ToString("0.0", CultureInfo.InvariantCulture) + " dB and the last at " + last.ToString("0.0", CultureInfo.InvariantCulture) + " dB (> 28)" + (rgb == null ? "" : ", got " + count));
			string facts2 = Describe(path);
			check(facts2.Contains("h264", StringComparison.Ordinal) && facts2.Contains("smpte170m", StringComparison.Ordinal) && facts2.Contains("30 fps", StringComparison.Ordinal),
				"mp4 through ffmpeg: ffmpeg names the stream H.264 at 30 fps with the BT.601 matrix it was converted with (" + facts2 + ")");

			// a full-HD frame's worth of stream arrives in many pieces and leaves in larger samples
			const int bw = 640;
			const int bh = 360;
			string bigPath = Path.Combine(folder, "encoder-large.mp4");
			int[][] big = [.. Enumerable.Range(0, 12).Select(i => TestFrame(bw, bh, i))];
			using (IFrameSink sink = EncoderMp4Writer.Create(bigPath, bw, bh, 30, 95, 0, FfmpegH264Encoder.Open))
			{
				foreach (int[] frame in big)
				{
					sink.Add(frame);
				}
			}
			Mp4Walk bigWalk = WalkMp4(File.ReadAllBytes(bigPath));
			byte[]? bigRgb = Decode(bigPath, bw, bh, out int bigCount);
			double bigPsnr = bigRgb == null || bigCount < 12 ? 0 : Psnr(bigRgb.AsSpan(11 * bw * bh * 3, bw * bh * 3), big[11], bw, bh);
			check(bigWalk.Problem == null && !bigWalk.MoovBeforeMdat && bigWalk.Samples.Count == 12 && bigCount == 12 && bigPsnr > 35,
				"mp4 through ffmpeg: 12 frames of 640 x 360 with no count planned, the index at the end, read back with the last frame at " + bigPsnr.ToString("0.0", CultureInfo.InvariantCulture) + " dB (> 35)" + (bigWalk.Problem != null ? " (" + bigWalk.Problem + ")" : ""));
		}
		catch (Exception ex)
		{
			check(false, "mp4 through ffmpeg: " + ex.Message);
		}
	}

	/// <summary>An MP4 written by the export job through ffmpeg: the sink lives on the job's sink thread, the container is written from the encoder's.</summary>
	private static void CheckH264Job(Action<bool, string> check, string folder, WaveDudeNext.Runs.FrameStore store, ExportLayer layer)
	{
		if (Ffmpeg == null || FfmpegH264Encoder.Probe().Obstacle != null)
		{
			return;
		}
		string path = Path.Combine(folder, "job-encoder.mp4");
		var job = new ExportJob(store, 0, 9, [new ExportTarget(layer, path, () => EncoderMp4Writer.Create(path, 64, 48, 30, 70, 10, FfmpegH264Encoder.Open))], null);
		job.Start();
		bool done = job.Completion.Wait(30000);
		Mp4Walk walk = done && File.Exists(path) ? WalkMp4(File.ReadAllBytes(path)) : new Mp4Walk { Problem = "no file" };
		check(done && job.State == ExportState.Completed && job.FramesDone == 10 && walk.Problem == null && walk.Samples.Count == 10,
			"job: an MP4 of 10 frames through ffmpeg and the in-house container" + (job.Error != null ? " (" + job.Error.Message + ")" : "") + (walk.Problem != null ? " (" + walk.Problem + ")" : ""));
	}

	/// <summary>PSNR of decoded RGB24 rows of <paramref name="stride"/> pixels against a frame of <paramref name="w"/> × <paramref name="h"/>: the picture's own part of a padded video.</summary>
	private static double PsnrPadded(ReadOnlySpan<byte> rgb, int[] frame, int w, int h, int stride)
	{
		var cropped = new byte[w * h * 3];
		for (int y = 0; y < h; y++)
		{
			rgb.Slice(y * stride * 3, w * 3).CopyTo(cropped.AsSpan(y * w * 3));
		}
		return Psnr(cropped, frame, w, h);
	}

	/// <summary>What ffmpeg says of a file's video stream: the line of its report that names the codec, the pixel format and the rate.</summary>
	private static string Describe(string path)
	{
		var info = new System.Diagnostics.ProcessStartInfo(Ffmpeg!)
		{
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (string arg in new[] { "-hide_banner", "-i", path })
		{
			info.ArgumentList.Add(arg);
		}
		using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
		string report = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return report.Split('\n').FirstOrDefault(l => l.Contains("Video:", StringComparison.Ordinal))?.Trim() ?? report.Trim();
	}

	/// <summary>What a walk through an MP4's boxes found.</summary>
	private sealed class Mp4Walk
	{
		public string? Problem { get; set; }

		public bool MoovBeforeMdat { get; set; }

		public bool EndsAtFileEnd { get; set; }

		public int Width { get; set; }

		public int Height { get; set; }

		public uint Timescale { get; set; }

		public uint Duration { get; set; }

		public string SampleEntry { get; set; } = "";

		public byte[]? Configuration { get; set; }

		public List<int> KeyFrames { get; } = [];

		public List<byte[]> Samples { get; } = [];
	}

	/// <summary>
	/// Walks an MP4 as a reader does: the top-level boxes to the file's end, the one track's tables, and every sample's
	/// bytes fetched by its offset and size. Without a list of key frames every sample counts as one.
	/// </summary>
	private static Mp4Walk WalkMp4(byte[] file)
	{
		var walk = new Mp4Walk();
		try
		{
			(string Type, int Start, int End)? Find(int from, int to, string type)
			{
				int at = from;
				while (at + 8 <= to)
				{
					long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
					string name = Encoding.ASCII.GetString(file, at + 4, 4);
					int head = 8;
					if (size == 1)
					{
						size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(at + 8));
						head = 16;
					}
					if (size < head || at + size > to)
					{
						throw new InvalidDataException("the box '" + name + "' at " + at + " has the size " + size);
					}
					if (name == type)
					{
						return (name, at + head, (int)(at + size));
					}
					at += (int)size;
				}
				return null;
			}
			(string Type, int Start, int End) Need(int from, int to, string type) => Find(from, to, type) ?? throw new InvalidDataException("no '" + type + "' box");
			// the top level: every box whole, the last ending with the file
			int top = 0;
			int moovAt = -1;
			int mdatAt = -1;
			while (top + 8 <= file.Length)
			{
				long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(top));
				string name = Encoding.ASCII.GetString(file, top + 4, 4);
				if (size == 1)
				{
					size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(top + 8));
				}
				if (size < 8 || top + size > file.Length)
				{
					throw new InvalidDataException("the top-level box '" + name + "' at " + top + " has the size " + size);
				}
				if (top == 0 && name != "ftyp")
				{
					throw new InvalidDataException("the file begins with '" + name + "'");
				}
				moovAt = name == "moov" ? top : moovAt;
				mdatAt = name == "mdat" ? top : mdatAt;
				top += (int)size;
			}
			walk.EndsAtFileEnd = top == file.Length;
			walk.MoovBeforeMdat = moovAt >= 0 && moovAt < mdatAt;
			var moov = Need(0, file.Length, "moov");
			var trak = Need(moov.Start, moov.End, "trak");
			var tkhd = Need(trak.Start, trak.End, "tkhd");
			walk.Width = (int)(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(tkhd.Start + 76)) >> 16);
			walk.Height = (int)(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(tkhd.Start + 80)) >> 16);
			var mdia = Need(trak.Start, trak.End, "mdia");
			var mdhd = Need(mdia.Start, mdia.End, "mdhd");
			walk.Timescale = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(mdhd.Start + 12));
			walk.Duration = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(mdhd.Start + 16));
			var hdlr = Need(mdia.Start, mdia.End, "hdlr");
			if (Encoding.ASCII.GetString(file, hdlr.Start + 8, 4) != "vide")
			{
				throw new InvalidDataException("the track's handler is not 'vide'");
			}
			var minf = Need(mdia.Start, mdia.End, "minf");
			var stbl = Need(minf.Start, minf.End, "stbl");
			var stsd = Need(stbl.Start, stbl.End, "stsd");
			if (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsd.Start + 4)) == 1)
			{
				int entry = stsd.Start + 8;
				int entryEnd = entry + (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry));
				walk.SampleEntry = Encoding.ASCII.GetString(file, entry + 4, 4);
				if (Find(entry + 86, entryEnd, "avcC") is { } avcc)
				{
					walk.Configuration = file[avcc.Start..avcc.End];
				}
			}
			if (Find(stbl.Start, stbl.End, "stss") is { } stss)
			{
				int keys = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stss.Start + 4));
				for (int i = 0; i < keys; i++)
				{
					walk.KeyFrames.Add((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stss.Start + 8 + 4 * i)));
				}
			}
			var stsz = Need(stbl.Start, stbl.End, "stsz");
			var co64 = Need(stbl.Start, stbl.End, "co64");
			int samples = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Start + 8));
			if (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(co64.Start + 4)) != samples)
			{
				throw new InvalidDataException("the offsets and the sizes differ in number");
			}
			var mdat = Need(0, file.Length, "mdat");
			for (int i = 0; i < samples; i++)
			{
				int size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Start + 12 + 4 * i));
				long offset = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(co64.Start + 8 + 8 * i));
				if (offset < mdat.Start || offset + size > mdat.End)
				{
					throw new InvalidDataException("sample " + (i + 1) + " lies outside the 'mdat' box");
				}
				walk.Samples.Add(file[(int)offset..(int)(offset + size)]);
			}
			if (Find(stbl.Start, stbl.End, "stss") == null)
			{
				walk.KeyFrames.AddRange(Enumerable.Range(1, samples));
			}
		}
		catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
		{
			walk.Problem = ex.Message;
		}
		return walk;
	}
}
