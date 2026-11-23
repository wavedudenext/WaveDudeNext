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

using System.Runtime.InteropServices.JavaScript;
using NLog;
using WaveDudeNext.Runs;

namespace WaveDudeNext.Browser;

/// <summary>
/// The frame cache of the browser head (2026-11-13, the user's word: a page has no drive to write to, its disk is
/// the storage the browser itself manages for it): the Origin Private File System, where the part of a run's frames
/// that does not fit the memory goes as it goes to a file on a drive in the desktop heads, up to <see cref="Room"/>.
/// Set as <see cref="FrameCache.Storage"/> by <c>Main</c>.
/// <para>
/// A browser gives synchronous access to such a file in a dedicated worker alone, one handle a file, so a worker of
/// the page's holds the files (<c>frame-storage-worker.js</c>) and reads and writes straight from and into the
/// program's memory, which the threads share; a call here goes to the page's scripts (<see cref="ScriptThread"/>,
/// <c>frame-storage.js</c>), from there to the worker, and the calling thread waits for the answer, as it waits in a
/// file call of an operating system. Measured in Edge 157 (2026-11-13): 2.9 GB/s written and 2.75 read in the worker
/// itself, a scattered read of 4 MB 1.1 ms; the hand-overs come on top.
/// </para>
/// <para>
/// A file here does not delete itself with the page, as a drive's does with its process: a page that is left gives
/// its files back as it goes (<c>frame-storage.js</c>, at the browser's <c>pagehide</c>), and what a browser that was
/// ended leaves is cleared by <see cref="Sweep"/> at the next start. Two tabs of the application share the storage
/// (2026-11-14, the user's question): each session's files carry its mark in their names, and the worker holds a lock
/// of the browser's named after the mark for as long as the page lives, so another tab's sweep leaves them, the one
/// just made and not yet opened too, which a sweep had removed before. Threads: as <see cref="IFrameStorage"/> and
/// <see cref="IFrameFile"/> say, and never the scripts' own.
/// </para>
/// </summary>
internal sealed partial class OpfsFrameStorage : IFrameStorage
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(OpfsFrameStorage).FullName!);
	private static Task? _module;
	private bool _reported;
	private long _othersReported;

	/// <summary>
	/// What a run's file may take of the browser's storage: 20 GB (2026-11-14, the user's choice, so that every
	/// example fits at full quality with the 3 GB of frames the memory holds; the largest need 19 to 20 GB in all).
	/// A figure of ours because the browser's is none: a page in Chromium is told that it may store 10 GiB more than
	/// it has, on every machine and whatever the disk (since 133, so that a site cannot tell incognito mode by it),
	/// while what is enforced is far more, up to 60 % of the disk. Where a browser or a full disk allows less than
	/// this, the run fails at the write that is refused, with the browser's words. One room for all of the
	/// application's tabs: what the others hold is taken off it (<see cref="AvailableBytes"/>).
	/// </summary>
	public const long Room = 20L << 30;

	/// <summary>
	/// This session's mark in its files' names, eight hexadecimal digits: two tabs of the application share the
	/// storage, and the worker reads the mark from a name to tell a living session's files (<c>sessionOf</c> in
	/// <c>frame-storage-worker.js</c>), so the form <see cref="NewPath"/> gives a name is agreed between the two.
	/// </summary>
	private readonly string _session = Guid.NewGuid().ToString("N")[..8];
	private int _sequence;

	/// <inheritdoc/>
	public string Place => "in the browser's storage for this page";

	/// <summary>A fresh file name: <c>run-&lt;time&gt;-&lt;session&gt;-&lt;n&gt;.wdframes</c>.</summary>
	public string NewPath() => $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{_session}-{Interlocked.Increment(ref _sequence)}{FrameCache.Extension}";

	/// <summary>
	/// <see cref="Room"/> less what the application's other tabs hold of the storage: the room is one for all of
	/// them (2026-11-21, the user's choice; until then each tab reckoned the whole of it for itself against the one
	/// storage). This session's own files are not taken off: the outgoing run's file goes as the new one comes. The
	/// browser is still asked what it reports, since the asking tells whether it has such a storage at all; that
	/// figure goes to the log, once, and the other tabs' share whenever it changes.
	/// </summary>
	/// <exception cref="IOException">The browser has no such storage.</exception>
	public long AvailableBytes()
	{
		(double reported, double others) = Call(async () =>
		{
			await Module();
			return (await JsFree(), await JsOthers());
		});
		if (!_reported)
		{
			_reported = true;
			Logger.Debug("Frame cache: a run's file may take {Room:0} GB of the browser's storage; the browser reports {Reported:0.0} GB free for the page",
				Room / 1073741824.0, reported / 1073741824.0);
		}
		long held = (long)Math.Max(0, others);
		if (held >> 20 != _othersReported >> 20)
		{
			_othersReported = held;
			Logger.Debug("Frame cache: the application's other tabs hold {Others:0.00} GB of that room", held / 1073741824.0);
		}
		return Math.Max(0, Room - held);
	}

	/// <inheritdoc/>
	public IFrameFile Create(string path) => new OpfsFrameFile(Call(async () =>
	{
		await Module();
		return await JsOpen(path);
	}));

	/// <summary>Removes what earlier sessions left behind; not waited for, the outcome goes to the log.</summary>
	public void Sweep() => ScriptThread.Post(
		async () =>
		{
			await Module();
			int removed = await JsSweep();
			if (removed > 0)
			{
				Logger.Debug("Removed {Count} leftover frame cache files from the browser's storage", removed);
			}
		},
		ex => Logger.Debug(ex, "The frame cache's leftovers in the browser's storage could not be removed"));

	/// <summary><see cref="Sweep"/>, waited for; returns how many files went.</summary>
	internal int SweepNow() => Call(async () =>
	{
		await Module();
		return await JsSweep();
	});

	/// <summary>The page's module, loaded once; on the scripts' thread.</summary>
	private static Task Module() => _module ??= JSHost.ImportAsync("frame-storage", "../frame-storage.js");

	/// <summary>A call to the page, waited for; what fails there is an <see cref="IOException"/> here, as a file call's failure is.</summary>
	private static T Call<T>(Func<Task<T>> call)
	{
		try
		{
			return ScriptThread.Await(call);
		}
		catch (InvalidOperationException ex) when (ex.InnerException is { } inner)
		{
			string words = inner.Message.StartsWith("Error: ", StringComparison.Ordinal) ? inner.Message[7..] : inner.Message;
			throw new IOException("The browser's storage: " + words, inner);
		}
	}

	[JSImport("free", "frame-storage")]
	private static partial Task<double> JsFree();

	[JSImport("others", "frame-storage")]
	private static partial Task<double> JsOthers();

	[JSImport("open", "frame-storage")]
	private static partial Task<int> JsOpen(string name);

	[JSImport("setLength", "frame-storage")]
	private static partial Task<int> JsSetLength(int file, [JSMarshalAs<JSType.Number>] long bytes);

	[JSImport("write", "frame-storage")]
	private static partial Task<int> JsWrite(int file, nint address, int length, [JSMarshalAs<JSType.Number>] long at);

	[JSImport("read", "frame-storage")]
	private static partial Task<int> JsRead(int file, nint address, int length, [JSMarshalAs<JSType.Number>] long at);

	[JSImport("close", "frame-storage")]
	private static partial Task<int> JsClose(int file);

	[JSImport("sweep", "frame-storage")]
	private static partial Task<int> JsSweep();

	/// <summary>
	/// A store's file in the browser's storage, by the number the worker knows it by. The bytes of a read or write are
	/// held in place for the call (the worker reads and writes the program's memory at their address).
	/// </summary>
	private sealed class OpfsFrameFile(int file) : IFrameFile
	{
		private volatile bool _closed;

		public void SetLength(long bytes)
		{
			ObjectDisposedException.ThrowIf(_closed, this);
			Call(() => JsSetLength(file, bytes));
		}

		public unsafe void Write(ReadOnlySpan<byte> data, long offset)
		{
			ObjectDisposedException.ThrowIf(_closed, this);
			fixed (byte* bytes = data)
			{
				nint address = (nint)bytes;
				int length = data.Length;
				Call(() => JsWrite(file, address, length, offset));
			}
		}

		public unsafe int Read(Span<byte> data, long offset)
		{
			ObjectDisposedException.ThrowIf(_closed, this);
			fixed (byte* bytes = data)
			{
				nint address = (nint)bytes;
				int length = data.Length;
				return Call(() => JsRead(file, address, length, offset));
			}
		}

		public void Close()
		{
			if (_closed)
			{
				return;
			}
			_closed = true;
			try
			{
				Call(() => JsClose(file));
			}
			catch (IOException ex)
			{
				// the next start's sweep takes it
				Logger.Debug(ex, "A frame cache file could not be removed from the browser's storage");
			}
		}
	}
}
