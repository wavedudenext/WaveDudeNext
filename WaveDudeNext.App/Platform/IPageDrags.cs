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
/// A drag the platform itself carries, for a head in which a drag of the framework's cannot leave the application
/// (<see cref="App.PageDrags"/>). That is the browser head (2026-11-22, the user's point that drag and drop between
/// two sessions was missing): Avalonia has no drag source in a browser, so a drag it starts lives inside the page
/// and ends at the page's edge, and a row could not be dragged into another tab's tree or picture, nor into a text
/// editor, as it can between two windows on a desktop.
/// <para>
/// A browser starts a drag of its own only from the user's press on something the page has marked as draggable, and
/// only where the press was left to the browser. So the application says beforehand what the pointer is over
/// (<see cref="Arm"/>); the head leaves a press on that to the browser, which starts the drag at its own threshold
/// with the text as its data, and the head says that it did (<see cref="Started"/>) and when the drag is over
/// (<see cref="Ended"/>). The control that armed asks at its press whether the press was the browser's
/// (<see cref="TakePress"/>) and then starts no drag of the framework's. Another tab and another program take the
/// text as a drop from outside; the application's own targets meet the drag as one from outside too, whose data the
/// framework cannot read on its way (<see cref="IPageDrops"/>), and ask here what it carries
/// (<see cref="Dragged"/>).
/// </para>
/// </summary>
public interface IPageDrags
{
	/// <summary>
	/// The pointer is over something a press may drag away: its text, as Copy writes it; null when it no longer is.
	/// UI thread; told to the platform without waiting.
	/// </summary>
	void Arm(string? text);

	/// <summary>
	/// Whether the press that has just reached the application was left to the platform, which then starts the drag
	/// itself; asked once a press, by the control that armed, and forgotten with the asking. UI thread.
	/// </summary>
	bool TakePress();

	/// <summary>The text of the drag of the application's that the platform carries right now; null without one. UI thread.</summary>
	string? Dragged { get; }

	/// <summary>The platform has started the drag of what was armed. Raised on the UI thread, before the drag's first event reaches a target.</summary>
	event EventHandler? Started;

	/// <summary>That drag is over, dropped or not, here or elsewhere. Raised on the UI thread, after a drop on the application itself has been handled.</summary>
	event EventHandler? Ended;
}
