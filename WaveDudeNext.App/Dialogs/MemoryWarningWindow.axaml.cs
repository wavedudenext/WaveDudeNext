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
using WaveDudeNext.Runs;

namespace WaveDudeNext.App.Dialogs;

/// <summary>
/// The memory warning's window on a desktop: the frame around a <see cref="MemoryWarningView"/>, which is the dialog
/// (the facts, the levers, the choice). Modal, owned by its caller's window; the close box cancels.
/// <see cref="MemoryWarningView.Show"/> makes and shows it. UI thread only.
/// </summary>
public partial class MemoryWarningWindow : Window
{
	/// <summary>The parameterless constructor the XAML loader needs; <see cref="MemoryWarningView.Show"/> builds the real window.</summary>
	public MemoryWarningWindow()
	{
		InitializeComponent();
	}

	/// <summary>Builds the window for a run; internal so the tests can show it without a modal owner.</summary>
	internal MemoryWarningWindow(RunSpec spec, long budget, long autoBudget, PreviewQuality current)
	{
		InitializeComponent();
		view.Set(spec, budget, autoBudget, current);
		view.CloseRequested += (_, _) => Close();
		Opened += (_, _) => view.Opened();
		AddHandler(KeyDownEvent, (_, e) => view.FrameKeyDown(e), RoutingStrategies.Tunnel);
		// the UI zoom on every window of the app, as upstream's Window style applied it
		WindowZoom.Attach(this);
	}

	/// <summary>The warning, the window's content.</summary>
	public MemoryWarningView View => view;
}
