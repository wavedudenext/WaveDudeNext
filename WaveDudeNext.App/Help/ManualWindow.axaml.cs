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

namespace WaveDudeNext.App.Help;

/// <summary>
/// The manual's window on a desktop (Help > Manual, F1): the frame around a <see cref="ManualView"/>, which is the
/// manual. Not modal, but owned by the main window (the user's choice): minimised and closed with it, kept above it,
/// opened once (<see cref="ShowOrActivate"/>). UI thread only.
/// </summary>
public partial class ManualWindow : Window
{
	private static ManualWindow? _instance;

	/// <summary>Builds the window around its view, which renders the manual.</summary>
	public ManualWindow()
	{
		InitializeComponent();
		view.CloseRequested += (_, _) => Close();
		Opened += (_, _) => view.Opened();
		AddHandler(KeyDownEvent, (_, e) => view.FrameKeyDown(e), RoutingStrategies.Tunnel);
		Closed += (_, _) =>
		{
			if (ReferenceEquals(_instance, this))
			{
				_instance = null;
			}
		};
		// the UI zoom on every window of the app, as upstream's Window style applied it
		WindowZoom.Attach(this);
	}

	/// <summary>The manual, the window's content.</summary>
	public ManualView View => view;

	/// <summary>
	/// Opens the manual owned by <paramref name="owner"/>, or brings the open one forward: there is one manual window at
	/// a time, as upstream kept it (MainWindow.Help.cs).
	/// </summary>
	public static ManualWindow ShowOrActivate(Window owner)
	{
		if (_instance == null)
		{
			_instance = new ManualWindow();
			_instance.Show(owner);
		}
		else
		{
			_instance.Activate();
		}
		return _instance;
	}
}
