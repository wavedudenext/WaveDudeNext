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

using Avalonia.Input;

namespace WaveDudeNext.App.Dialogs;

/// <summary>
/// A dialog's content as its frame sees it (<see cref="DialogFrame"/>): the frame, a window of the dialog's own on a
/// desktop or the overlay's frame in a browser, tells the view when it is shown and what key was pressed in it, and
/// closes when the view asks. Closed by the frame's own means (a window's close box, the overlay frame's), a view
/// hears nothing: what it answers then is what it answered before any button was pressed, its cancel answer. UI
/// thread only.
/// </summary>
public interface IDialogView
{
	/// <summary>The dialog is over (a button was pressed, Esc): its frame closes. What was answered is the view's own to say.</summary>
	event EventHandler? CloseRequested;

	/// <summary>The frame shows the dialog, laid out: the control the dialog starts on takes the keyboard focus.</summary>
	void Opened();

	/// <summary>
	/// A key was pressed with the focus anywhere in the frame, before the focused control sees it (the frame's
	/// tunnelling handler): Esc, the letters the buttons underline. The view marks what it took as handled.
	/// </summary>
	void FrameKeyDown(KeyEventArgs e);
}
