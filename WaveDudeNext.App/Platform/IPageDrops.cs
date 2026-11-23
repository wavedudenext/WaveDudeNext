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
//   Mark <mark@pab-ai.com>, co-author of the AvaloniaUI port
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

namespace WaveDudeNext.App.Platform;

/// <summary>
/// The last drop from outside as the head itself read it, for a head to which the framework hands such a drop
/// without its data (<see cref="App.PageDrops"/>). That is the browser head (2026-11-20): Avalonia's script gives
/// .NET the browser's live items of a drop, which the browser empties when the drop's handlers have returned, and
/// on the threaded runtime .NET reads them only after that, so every drop there arrived with no format at all. The
/// head's own script takes the text and the files while the browser still has them; the application's drop targets
/// ask here when a drop arrives empty. What is answered is the drop being handled: a new drag forgets the last one.
/// </summary>
public interface IPageDrops
{
	/// <summary>The dropped text; null for none. UI thread; answers at once.</summary>
	string? Text();

	/// <summary>The names of the dropped files, in the drop's order; empty for none. UI thread; answers at once.</summary>
	IReadOnlyList<string> Files();

	/// <summary>The contents of the file at <paramref name="index"/> of <see cref="Files"/>. Any thread.</summary>
	/// <exception cref="IOException">The file cannot be read, or is larger than a file of the application's can be.</exception>
	Task<byte[]> ReadAsync(int index);
}
