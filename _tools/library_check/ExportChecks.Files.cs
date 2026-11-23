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

using System.Collections.Concurrent;
using System.Globalization;
using WaveDudeNext;
using WaveDudeNext.Export;

namespace LibraryCheck;

// An export's files through a head's own files (2026-11-15, for the browser head, where a file is the one the user
// picked and takes bytes at a position): the library's stream over such a file against a stream in memory, operation
// for operation, and every format's files written through one byte for byte the files written to the disk.
internal static partial class ExportChecks
{
	/// <summary>A head's file in memory: takes whole arrays at positions, counts them, and spoils each array once it has copied it, the array being the caller's again.</summary>
	private sealed class MemoryExportFile(string path, ConcurrentDictionary<string, MemoryExportFile> closed) : IExportFile
	{
		private readonly MemoryStream _bytes = new();

		public int Pieces { get; private set; }

		public int Largest { get; private set; }

		public bool IsClosed { get; private set; }

		public bool WrittenAfterClose { get; private set; }

		public void Write(long position, byte[] bytes)
		{
			WrittenAfterClose |= IsClosed;
			Pieces++;
			Largest = Math.Max(Largest, bytes.Length);
			_bytes.Position = position;
			_bytes.Write(bytes);
			Array.Fill(bytes, (byte)0xEE);
		}

		public void SetLength(long length)
		{
			WrittenAfterClose |= IsClosed;
			_bytes.SetLength(length);
		}

		public void Close()
		{
			WrittenAfterClose |= IsClosed;
			IsClosed = true;
			closed[path] = this;
		}

		public byte[] ToArray() => _bytes.ToArray();
	}

	/// <summary>
	/// The stream over a head's file does what a file's stream does, and the sinks write the same files through it:
	/// random writes, patches, seeks beyond the end and cuts against a stream in memory; then every format, twelve
	/// frames of 640 × 480 with noise in them, to the disk and through the opener.
	/// </summary>
	private static void CheckExportFiles(Action<bool, string> check, string folder)
	{
		Console.WriteLine("--- export: files of a head's own (bytes at a position, a few megabytes a piece)");
		var closed = new ConcurrentDictionary<string, MemoryExportFile>(StringComparer.Ordinal);
		Func<string, IExportFile?>? before = ExportFiles.Opener;
		try
		{
			// the stream itself: the same operations on it and on a stream in memory
			ExportFiles.Opener = path => new MemoryExportFile(path, closed);
			var random = new Pcg32(20261115);
			var reference = new MemoryStream();
			bool agree = true;
			string streamPath = Path.Combine(folder, "stream.bin");
			using (Stream stream = ExportFiles.Create(streamPath))
			{
				check(stream is not FileStream && stream.CanWrite && stream.CanSeek && !stream.CanRead, "files: with an opener the sink's stream is the library's over the head's file, writable and seekable");
				void Both(Action<Stream> operation)
				{
					operation(stream);
					operation(reference);
					agree &= stream.Position == reference.Position && stream.Length == reference.Length;
				}
				// a header reserved by its length, as the AVI writer does, then one write of more than two pieces
				Both(s => s.SetLength(2048));
				Both(s => s.Position = 2048);
				byte[] large = Noise(random, 2 * ExportFileStream.PieceBytes + 12345);
				Both(s => s.Write(large));
				for (int i = 0; i < 400; i++)
				{
					byte[] bytes = Noise(random, (int)(random.NextUInt32() % (i % 40 == 0 ? 3_000_000 : 70_000)) + 1);
					switch (random.NextUInt32() % 10)
					{
						case 0: // back into what is written, a patch
							long back = random.NextUInt32() % reference.Length;
							Both(s => s.Position = back);
							Both(s => s.Write(bytes, 0, Math.Min(bytes.Length, 64)));
							Both(s => s.Seek(0, SeekOrigin.End));
							break;
						case 1: // beyond the end: zeros between
							long beyond = 1 + (random.NextUInt32() % 5000);
							Both(s => s.Seek(beyond, SeekOrigin.End));
							Both(s => s.Write(bytes));
							break;
						case 2:
							Both(s => s.Flush());
							break;
						case 3 when i % 7 == 0: // cut, or extended
							long length = Math.Max(1, reference.Length + (long)(random.NextUInt32() % 20_000) - 10_000);
							Both(s => s.SetLength(length));
							Both(s => s.Seek(0, SeekOrigin.End));
							break;
						case 4:
							Both(s => s.WriteByte(bytes[0]));
							break;
						default:
							Both(s => s.Write(bytes));
							break;
					}
				}
				// the header last, as the writers fill theirs in
				byte[] header = Noise(random, 2048);
				Both(s => s.Position = 0);
				Both(s => s.Write(header));
			}
			MemoryExportFile? made = closed.GetValueOrDefault(streamPath);
			check(agree, "files: position and length agree with a stream in memory after every one of 400 operations");
			check(made != null && made.IsClosed && !made.WrittenAfterClose && made.ToArray().AsSpan().SequenceEqual(reference.ToArray()),
				string.Create(CultureInfo.InvariantCulture, $"files: writes in a row, patches, seeks beyond the end and cuts leave the bytes a stream in memory holds ({reference.Length / 1048576.0:0.0} MB)"));
			check(made != null && made.Largest == ExportFileStream.PieceBytes && made.Pieces < 1200,
				"files: no piece larger than " + (ExportFileStream.PieceBytes >> 20) + " MB, and writes in a row gathered (" + made?.Pieces + " pieces for 400 operations)");
			check(!File.Exists(streamPath), "files: nothing is written at the path itself");

			// every format: to the disk, then through the opener, the same frames
			const int w = 640;
			const int h = 480;
			const int count = 12;
			int[][] frames = [.. Enumerable.Range(0, count).Select(i => NoisyFrame(random, w, h, i))];
			Half[][] planes = [.. frames.Select(frame => frame.Select(pixel => (Half)((pixel & 0xFF) / 255f)).ToArray())];
			var jpeg = ExportFormats.PreferredJpeg;
			(string Name, ExportFormat Format, bool Still)[] formats =
			[
				("avi", ExportFormat.Avi, false), ("mov", ExportFormat.Mov, false), ("mov-png", ExportFormat.MovPng, false), ("mpg", ExportFormat.Mpeg1, false),
				("png", ExportFormat.Png, false), ("jpg", ExportFormat.Jpeg, false), ("exr", ExportFormat.Exr, false), ("still-png", ExportFormat.Png, true), ("still-exr", ExportFormat.Exr, true),
			];
			try
			{
				ExportFormats.PreferredJpeg = null; // the in-house encoder, the one a head without Windows has
				foreach ((string name, ExportFormat format, bool still) in formats)
				{
					string direct = Path.Combine(folder, "files-disk", name);
					string through = Path.Combine(folder, "files-head", name);
					Directory.CreateDirectory(direct);
					Directory.CreateDirectory(through);
					closed.Clear();
					foreach (string target in new[] { direct, through })
					{
						ExportFiles.Opener = target == through ? path => new MemoryExportFile(path, closed) : null;
						string path = Path.Combine(target, "out" + ExportFormats.Extension(format));
						using IFrameSink sink = still ? ExportFormats.OpenStill(format, path, w, h, 85) : ExportFormats.Open(format, path, w, h, 30, 85, count, 3);
						for (int i = 0; i < (still ? 1 : count); i++)
						{
							if (sink.WantsHalves)
							{
								sink.Add((ReadOnlySpan<Half>)planes[i]);
							}
							else
							{
								sink.Add(frames[i]);
							}
						}
						sink.Close();
					}
					string[] onDisk = Directory.GetFiles(direct, "*", SearchOption.AllDirectories);
					bool same = onDisk.Length > 0 && onDisk.Length == closed.Count;
					long bytes = 0;
					int pieces = 0;
					foreach (string file in onDisk)
					{
						string twin = Path.Combine(through, Path.GetRelativePath(direct, file));
						byte[] expected = File.ReadAllBytes(file);
						bytes += expected.Length;
						same &= closed.TryGetValue(twin, out MemoryExportFile? head) && !head.WrittenAfterClose && head.ToArray().AsSpan().SequenceEqual(expected);
						pieces += head?.Pieces ?? 0;
					}
					check(same && Directory.GetFiles(through, "*", SearchOption.AllDirectories).Length == 0,
						string.Create(CultureInfo.InvariantCulture, $"files, {name}: {onDisk.Length} file(s), {bytes / 1048576.0:0.0} MB, through a head's files byte for byte those on the disk, in {pieces} pieces, none at the path"));
				}
			}
			finally
			{
				ExportFormats.PreferredJpeg = jpeg;
			}
		}
		finally
		{
			ExportFiles.Opener = before;
		}
	}

	private static byte[] Noise(Pcg32 random, int length)
	{
		var bytes = new byte[length];
		uint next = 0;
		for (int i = 0; i < length; i++)
		{
			if ((i & 3) == 0)
			{
				next = random.NextUInt32();
			}
			bytes[i] = (byte)(next >> (8 * (i & 3)));
		}
		return bytes;
	}

	/// <summary>The test frame with noise in its lower half, so the lossless formats' files cross several pieces.</summary>
	private static int[] NoisyFrame(Pcg32 random, int w, int h, int index)
	{
		int[] pixels = TestFrame(w, h, index);
		for (int i = w * h / 2; i < pixels.Length; i++)
		{
			pixels[i] = unchecked((int)(0xFF000000 | random.NextUInt32()));
		}
		return pixels;
	}
}
