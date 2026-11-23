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
using Avalonia.Threading;
using WaveDudeNext.App.Theming;

namespace WaveDudeNext.App.Help;

/// <summary>
/// The About dialog's window on a desktop (Help > About): the frame around an <see cref="AboutView"/>, which is the
/// dialog. Modal, owned by the main window, resizable; what the window adds is its sizing around the license
/// document. <see cref="AboutView.Show"/> makes and shows it. UI thread only.
/// </summary>
public partial class AboutWindow : Window
{
	/// <summary>Builds the window around its view.</summary>
	public AboutWindow()
	{
		InitializeComponent();
		view.CloseRequested += (_, _) => Close();
		view.LicenseToggled += (_, _) => OnLicenseToggled();
		Opened += (_, _) => view.Opened();
		AddHandler(KeyDownEvent, (_, e) => view.FrameKeyDown(e), RoutingStrategies.Tunnel);
		// the UI zoom on every window of the app, as upstream's Window style applied it
		WindowZoom.Attach(this);
	}

	/// <summary>The dialog, the window's content.</summary>
	public AboutView View => view;

	/// <summary>Whether the license text is shown, for the tests.</summary>
	public bool IsLicenseShown => view.IsLicenseShown;

	/// <summary>
	/// The window follows its content while the document is hidden (it grows by the box's fixed height when the
	/// document appears and shrinks when it goes) and is sized by hand while the document is shown, since a window
	/// sized to content snaps back after a resize and a star row under content sizing would take the whole document;
	/// the switch waits for the layout that lets the window grow, then frees the box to fill its row.
	/// </summary>
	private void OnLicenseToggled()
	{
		if (view.IsLicenseShown)
		{
			Dispatcher.UIThread.Post(() =>
			{
				SizeToContent = SizeToContent.Manual;
				view.FillLicense(true);
			}, DispatcherPriority.Loaded);
		}
		else
		{
			view.FillLicense(false);
			SizeToContent = SizeToContent.Height;
		}
	}
}
