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

using Avalonia.Controls;
using Avalonia.Interactivity;
using WaveDudeNext.App.Theming;

namespace WaveDudeNext.App.Dialogs;

/// <summary>
/// The message box's window on a desktop: the frame around a <see cref="MessageView"/>, which is the dialog (its text,
/// its buttons, its answer). Modal, owned by its caller's window; the close box closes with the set's cancel answer.
/// <see cref="MessageView.Show"/> makes and shows it. UI thread only.
/// </summary>
public partial class MessageWindow : Window
{
	/// <summary>The parameterless constructor the XAML loader needs; <see cref="MessageView.Show"/> builds the real window.</summary>
	public MessageWindow() : this("", MessageKind.Question, MessageButtons.Ok)
	{
	}

	/// <summary>Builds the window for a text, a kind and a button set; internal so the tests can show it without a modal owner.</summary>
	internal MessageWindow(string text, MessageKind kind, MessageButtons buttons)
	{
		InitializeComponent();
		view.Set(text, kind, buttons);
		view.CloseRequested += (_, _) => Close();
		Opened += (_, _) => view.Opened();
		AddHandler(KeyDownEvent, (_, e) => view.FrameKeyDown(e), RoutingStrategies.Tunnel);
		// the UI zoom on every window of the app, as upstream's Window style applied it
		WindowZoom.Attach(this);
	}

	/// <summary>The message, the window's content.</summary>
	public MessageView View => view;
}
