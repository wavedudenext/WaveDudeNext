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
using System.Runtime.InteropServices.JavaScript;
using WaveDudeNext.App.Platform;

namespace WaveDudeNext.Browser;

/// <summary>
/// The drops from outside the page as the page's own script reads them (<c>page-drop.js</c>, which says why: the
/// framework's drop reaches the program without its data on the threaded runtime), for the application's drop
/// targets (<see cref="IPageDrops"/>). Every call goes to the thread the page's scripts are called from and is
/// waited for (<see cref="ScriptThread"/>): the two that the UI thread makes while it handles a drop answer from
/// what the script holds already, a few tenths of a millisecond; a file's bytes are read on a thread of the pool.
/// </summary>
internal sealed partial class PageDrops : IPageDrops
{
	/// <summary>The largest file read: far above any project file with its scene image, far below the program's memory.</summary>
	private const long MostBytes = 256L << 20;

	private PageDrops()
	{
	}

	/// <summary>Loads the script, whose listeners are the page's from then on. On the thread the page's scripts are called from.</summary>
	public static async Task<PageDrops> CreateAsync()
	{
		// beside the page, wherever that is served from: the address counts from the runtime's folder
		await JSHost.ImportAsync("page-drop", "../page-drop.js");
		return new PageDrops();
	}

	/// <inheritdoc/>
	public string? Text()
	{
		string text = ScriptThread.Invoke(JsText);
		return text.Length > 0 ? text : null;
	}

	/// <inheritdoc/>
	public IReadOnlyList<string> Files() => ScriptThread.Invoke(() =>
	{
		string[] names = new string[JsCount()];
		for (int i = 0; i < names.Length; i++)
		{
			names[i] = JsName(i);
		}
		return names;
	});

	/// <inheritdoc/>
	public Task<byte[]> ReadAsync(int index) => Task.Run(() =>
	{
		try
		{
			double length = ScriptThread.Invoke(() => JsLength(index));
			if (length > MostBytes)
			{
				throw new IOException($"The dropped file has {length / (1 << 20):0} MB, more than a file of this application's can have.");
			}
			byte[] bytes = new byte[ScriptThread.Await(() => JsRead(index))];
			// pinned by a handle for the script's copy: it is made on another thread, while this one waits
			GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
			try
			{
				nint address = pin.AddrOfPinnedObject();
				ScriptThread.Invoke(() => JsCopy(address));
			}
			finally
			{
				pin.Free();
			}
			return bytes;
		}
		catch (InvalidOperationException ex)
		{
			// the script's own refusal (a file the browser no longer hands over, a drop forgotten for a newer drag)
			throw new IOException("The dropped file could not be read: " + ex.Message, ex);
		}
	});

	[JSImport("text", "page-drop")]
	private static partial string JsText();

	[JSImport("count", "page-drop")]
	private static partial int JsCount();

	[JSImport("name", "page-drop")]
	private static partial string JsName(int index);

	[JSImport("length", "page-drop")]
	private static partial double JsLength(int index);

	[JSImport("read", "page-drop")]
	private static partial Task<int> JsRead(int index);

	[JSImport("copy", "page-drop")]
	private static partial void JsCopy(nint address);
}
