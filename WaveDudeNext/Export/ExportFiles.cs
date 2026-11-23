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

using System.Buffers;

namespace WaveDudeNext.Export;

/// <summary>
/// An export's file where a head's files are not paths (<see cref="ExportFiles.Opener"/>): bytes written at a
/// position, as the browser's own stream into a file the user picked takes them. The library's stream over it
/// (<see cref="ExportFiles.Create"/>) gathers what a sink writes into pieces of a few megabytes, so an implementation
/// is called a few times a second at most, and by one thread at a time, the one that writes the file.
/// </summary>
public interface IExportFile
{
	/// <summary>
	/// Writes <paramref name="bytes"/>, the whole array, at <paramref name="position"/> and returns when they are
	/// written or taken over: the array is the caller's again afterwards, and the caller's waiting is what keeps
	/// an export from running ahead of a slow file. A position beyond the file's end leaves zeros between, as a file does.
	/// </summary>
	/// <param name="position">Bytes from the file's start.</param>
	/// <param name="bytes">What to write, all of it.</param>
	void Write(long position, byte[] bytes);

	/// <summary>Cuts the file to <paramref name="length"/> bytes, or extends it with zeros.</summary>
	void SetLength(long length);

	/// <summary>The file is complete: closed, and with that kept, where the file is one that exists only once it is closed.</summary>
	void Close();
}

/// <summary>
/// Where the files of an export are made: every sink that writes a file (the video writers, the image sequences,
/// the stills, OpenEXR) opens it through <see cref="Create"/>. On a desktop that is the file at the path, as the
/// sinks have always opened it. A head whose files are not paths sets <see cref="Opener"/> once at its start, as it
/// sets its GPU engine and its frame storage (2026-11-15, the browser head, the user's point: a browser is always
/// short of room, so an export's file is written into the file the user picked as it is made, a few megabytes at a
/// time, and never lies in the page's memory; until then the sinks wrote into the runtime's temporary file system,
/// which is that memory, and the file was copied out afterwards).
/// </summary>
public static class ExportFiles
{
	/// <summary>
	/// A head's own way to make an export's file: given the path the export was told, the file to write, or null
	/// for a file of the file system at that path. Called on the thread that writes the file (an export's sink
	/// thread, a sequence's encoder threads, several at once, each for a file of its own). Null, the default, for none.
	/// </summary>
	public static Func<string, IExportFile?>? Opener { get; set; }

	/// <summary>
	/// The stream an export's file at <paramref name="path"/> is written through: over the head's file
	/// (<see cref="Opener"/>), else the file itself, created or emptied, for writing, with a 64 K buffer. Either
	/// can be sought in and cut, as the video writers do when they go back to their headers, and is complete when disposed.
	/// </summary>
	/// <param name="path">The file.</param>
	/// <param name="share">What others may do with the file meanwhile, where it is a file of the file system.</param>
	public static Stream Create(string path, FileShare share = FileShare.Read) =>
		Opener?.Invoke(path) is { } file ? new ExportFileStream(file) : new FileStream(path, FileMode.Create, FileAccess.Write, share, 1 << 16);
}

/// <summary>
/// A stream for writing over an <see cref="IExportFile"/>: what is written in a row is gathered in one buffer of
/// <see cref="PieceBytes"/> and handed over when the buffer is full, when a write goes elsewhere (a writer going back
/// to its header), at <see cref="Flush"/> and at the end; a full buffer as it is, a part of one as an array of its
/// own, since the file takes whole arrays. One thread at a time. The buffers are the shared pool's, so a sequence of
/// thousands of small files does not make one for each.
/// </summary>
internal sealed class ExportFileStream(IExportFile file) : Stream
{
	/// <summary>The size of a piece handed to the file, 4 MB: half a full-HD frame of an uncompressed video.</summary>
	public const int PieceBytes = 4 << 20;

	private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(PieceBytes);
	private long _start;
	private int _count;
	private long _position;
	private long _length;

	/// <inheritdoc/>
	public override bool CanRead => false;

	/// <inheritdoc/>
	public override bool CanSeek => true;

	/// <inheritdoc/>
	public override bool CanWrite => _buffer != null;

	/// <inheritdoc/>
	public override long Length => _length;

	/// <inheritdoc/>
	public override long Position
	{
		get => _position;
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegative(value);
			_position = value;
		}
	}

	/// <inheritdoc/>
	public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

	/// <inheritdoc/>
	public override void Write(ReadOnlySpan<byte> bytes)
	{
		byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(ExportFileStream));
		while (bytes.Length > 0)
		{
			if (_count > 0 && _position != _start + _count)
			{
				HandOver();
			}
			if (_count == 0)
			{
				_start = _position;
			}
			int taken = Math.Min(bytes.Length, buffer.Length - _count);
			bytes[..taken].CopyTo(buffer.AsSpan(_count));
			bytes = bytes[taken..];
			_count += taken;
			_position += taken;
			_length = Math.Max(_length, _position);
			if (_count == buffer.Length)
			{
				HandOver();
			}
		}
	}

	/// <inheritdoc/>
	public override void WriteByte(byte value) => Write([value]);

	/// <summary>What is gathered goes to the file.</summary>
	private void HandOver()
	{
		if (_count == 0)
		{
			return;
		}
		byte[] buffer = _buffer!;
		int count = _count;
		_count = 0;
		file.Write(_start, count == buffer.Length ? buffer : buffer.AsSpan(0, count).ToArray());
	}

	/// <inheritdoc/>
	public override void Flush()
	{
		ObjectDisposedException.ThrowIf(_buffer == null, this);
		HandOver();
	}

	/// <inheritdoc/>
	public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
	{
		SeekOrigin.Begin => offset,
		SeekOrigin.Current => _position + offset,
		_ => _length + offset,
	};

	/// <inheritdoc/>
	public override void SetLength(long value)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(value);
		ObjectDisposedException.ThrowIf(_buffer == null, this);
		HandOver();
		file.SetLength(value);
		_length = value;
		_position = Math.Min(_position, value);
	}

	/// <inheritdoc/>
	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("An export's file is written, not read.");

	/// <inheritdoc/>
	protected override void Dispose(bool disposing)
	{
		if (disposing && _buffer is { } buffer)
		{
			try
			{
				HandOver();
			}
			finally
			{
				_buffer = null;
				ArrayPool<byte>.Shared.Return(buffer);
				file.Close();
			}
		}
		base.Dispose(disposing);
	}
}
