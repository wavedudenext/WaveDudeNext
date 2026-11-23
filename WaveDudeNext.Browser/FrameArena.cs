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

using System.Runtime.InteropServices;
using WaveDudeNext.Runs;

namespace WaveDudeNext.Browser;

/// <summary>
/// The frames' part of the browser head's memory (2026-11-13, the user's word that the head is to use its memory to
/// the full): the program is built with its memory's ceiling at 4 GB, the most 32 bits address, keeps the lower part,
/// up to <see cref="FrameMemory.BrowserReserve"/>, for everything the runtime and the libraries allocate, and hands
/// the 3 GB above it out here, to a run's frames alone (<see cref="FrameMemory.Allocator"/>). <c>Native/framearena.c</c>
/// says why the two are kept apart (the runtime cannot start a thread whose structure lies above 2 GB) and holds the
/// heap to its part.
/// <para>
/// Blocks are taken first-fit from a list of the free ranges and merged with their neighbours when returned; a
/// run's frames are a few hundred blocks of one size, so the list stays short and the blocks lie end to end. The
/// memory of a block is made usable when the block is first handed out (the program's memory grows to its end);
/// it is never given back to the browser, as none of a WebAssembly program's memory is. An address up here is a
/// negative number where it is read as a signed one, so it is reckoned unsigned throughout. Any thread.
/// </para>
/// </summary>
internal sealed class FrameArena : IFrameAllocator
{
	/// <summary>A WebAssembly page, the unit the two parts are laid out in.</summary>
	private const uint Page = 64 * 1024;

	/// <summary>What a block's size is rounded up to, so every block starts at a multiple of it.</summary>
	private const uint Alignment = 64;

	private readonly Lock _gate = new();
	private readonly List<(uint Start, uint Size)> _free = [];
	private readonly Dictionary<uint, uint> _taken = [];
	private readonly uint _start;
	private readonly uint _end;

	private FrameArena(uint start, uint end)
	{
		_start = start;
		_end = end;
		_free.Add((start, end - start));
	}

	/// <summary>
	/// Sets the two parts up, once, at the program's start, before anything large is allocated: the heap is held
	/// below <paramref name="reserve"/> from now on. Returns the allocator for <see cref="FrameMemory.Allocator"/>:
	/// the arena; or, where the program's memory ends at 2 GB or below (a build without the higher ceiling), null,
	/// since nothing lies above 2 GB there and the default allocator serves; or, where the parts could not be made,
	/// one that has no memory at all, so every frame goes to the frame cache rather than into the heap.
	/// </summary>
	/// <param name="reserve">Bytes of the program's memory, from its start, that stay the heap's.</param>
	/// <param name="granted">
	/// The ceiling of the memory the browser gave, in bytes: the page's script makes the memory and falls back to a
	/// lower ceiling where the browser refuses the 4 GB the program is built for (main.js), which the C library
	/// does not learn of. A value above the built ceiling, or none handed over, means the built one.
	/// </param>
	/// <param name="layout">What was set up, for the log.</param>
	public static IFrameAllocator? TryCreate(long reserve, long granted, out string layout)
	{
		const double GiB = 1073741824.0;
		uint built = wavedude_heap_ceiling();
		uint heapEnd = wavedude_heap_end();
		if (built <= 1u << 31)
		{
			layout = $"the program's memory ends at {built / GiB:0.00} GB: no part of its own for the frames";
			return null;
		}
		// where the browser gave less, the two parts are made all the same: the heap is held below the reserve, so the
		// frames cannot crowd the program out of what memory there is, and they get what lies above, be it little
		uint ceiling = (uint)Math.Clamp(granted, 2L * Page, built);
		string given = ceiling < built ? $" (the browser gave no more; the program is built for {built / GiB:0.00} GB)" : "";
		uint limit = (uint)Math.Clamp(reserve, Page, ceiling - Page) / Page * Page;
		if (wavedude_partition(limit) == 0)
		{
			layout = $"the heap (at {heapEnd / GiB:0.00} GB) could not be held below {limit / GiB:0.00} GB: the frames get no memory, all of them go to the frame cache{given}";
			return new NoMemory();
		}
		uint end = ceiling / Page * Page;
		layout = $"the program's memory ends at {ceiling / GiB:0.00} GB{given}; the heap, {heapEnd / GiB:0.00} GB of it in use, ends at {wavedude_heap_end() / GiB:0.00} GB for good, the {(end - limit) / GiB:0.00} GB from {limit / GiB:0.00} GB on are the frames'";
		return new FrameArena(limit, end);
	}

	/// <inheritdoc/>
	public long Capacity => _end - _start;

	/// <summary>Bytes handed out now.</summary>
	public long Taken
	{
		get
		{
			lock (_gate)
			{
				long sum = 0;
				foreach (uint size in _taken.Values)
				{
					sum += size;
				}
				return sum;
			}
		}
	}

	/// <inheritdoc/>
	public nint Allocate(long bytes)
	{
		if (bytes <= 0 || bytes > Capacity)
		{
			throw new OutOfMemoryException("the frames' memory has no block of that size");
		}
		uint size = (uint)((bytes + Alignment - 1) / Alignment * Alignment);
		lock (_gate)
		{
			for (int i = 0; i < _free.Count; i++)
			{
				(uint start, uint room) = _free[i];
				if (room < size)
				{
					continue;
				}
				// the block's memory exists from here on; a browser that will not give it leaves the range free
				if (wavedude_commit(start + size) == 0)
				{
					break;
				}
				if (room == size)
				{
					_free.RemoveAt(i);
				}
				else
				{
					_free[i] = (start + size, room - size);
				}
				_taken[start] = size;
				return unchecked((nint)start);
			}
		}
		throw new OutOfMemoryException("the frames' memory is full");
	}

	/// <inheritdoc/>
	public void Free(nint block)
	{
		uint start = unchecked((uint)block);
		lock (_gate)
		{
			if (!_taken.Remove(start, out uint size))
			{
				return;
			}
			// back into the list at its place, merged with a free neighbour on either side
			int at = 0;
			while (at < _free.Count && _free[at].Start < start)
			{
				at++;
			}
			if (at < _free.Count && start + size == _free[at].Start)
			{
				size += _free[at].Size;
				_free.RemoveAt(at);
			}
			if (at > 0 && _free[at - 1].Start + _free[at - 1].Size == start)
			{
				start = _free[at - 1].Start;
				size += _free[at - 1].Size;
				_free.RemoveAt(--at);
			}
			_free.Insert(at, (start, size));
		}
	}

	[DllImport("framearena")]
	private static extern uint wavedude_heap_ceiling();

	[DllImport("framearena")]
	private static extern uint wavedude_heap_end();

	[DllImport("framearena")]
	private static extern int wavedude_partition(uint limit);

	[DllImport("framearena")]
	private static extern int wavedude_commit(uint end);

	/// <summary>No memory for frames: every request is refused, so a store keeps all its frames in its file.</summary>
	private sealed class NoMemory : IFrameAllocator
	{
		public long Capacity => 0;

		public nint Allocate(long bytes) => throw new OutOfMemoryException("the frames have no memory in this session");

		public void Free(nint block)
		{
		}
	}
}
