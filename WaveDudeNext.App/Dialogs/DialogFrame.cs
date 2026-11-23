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

using Avalonia;
using Avalonia.Controls;

namespace WaveDudeNext.App.Dialogs;

/// <summary>
/// What a dialog's content says of the frame around it, set on the content's root in its markup. A dialog is a view
/// (an <see cref="IDialogView"/>: the content, its markup and its logic) in a frame (2026-11-15, the user's design,
/// so the same markup serves both heads): on a desktop the frame is a window of the dialog's own, which is the view
/// and nothing else under a title bar and keeps its sizes in its own markup, a window being resizable or not and
/// having minimum sizes; in a browser, which has no windows, the frame is drawn over the main view
/// (<see cref="DialogOverlay"/>). Both take the <c>Title</c> from here; <c>Width</c> and <c>Height</c> are the
/// content's size in the overlay's frame, in the window's units (the client area at zoom 1), which the overlay
/// never lets exceed the page.
/// </summary>
public static class DialogFrame
{
	/// <summary>The frame's title: the window's title bar, the overlay frame's title strip.</summary>
	public static readonly AttachedProperty<string?> TitleProperty =
		AvaloniaProperty.RegisterAttached<Control, string?>("Title", typeof(DialogFrame));

	/// <summary>The content's width where the frame is not a window; NaN, the default, for the content's own.</summary>
	public static readonly AttachedProperty<double> WidthProperty =
		AvaloniaProperty.RegisterAttached<Control, double>("Width", typeof(DialogFrame), double.NaN);

	/// <summary>The content's height where the frame is not a window; NaN, the default, for the content's own.</summary>
	public static readonly AttachedProperty<double> HeightProperty =
		AvaloniaProperty.RegisterAttached<Control, double>("Height", typeof(DialogFrame), double.NaN);

	/// <summary>The title of the frame around <paramref name="view"/>.</summary>
	public static string? GetTitle(Control view) => view.GetValue(TitleProperty);

	/// <summary>Sets the title of the frame around <paramref name="view"/>.</summary>
	public static void SetTitle(Control view, string? value) => view.SetValue(TitleProperty, value);

	/// <summary>The width <paramref name="view"/> has in an overlay's frame; NaN for its own.</summary>
	public static double GetWidth(Control view) => view.GetValue(WidthProperty);

	/// <summary>Sets the width <paramref name="view"/> has in an overlay's frame.</summary>
	public static void SetWidth(Control view, double value) => view.SetValue(WidthProperty, value);

	/// <summary>The height <paramref name="view"/> has in an overlay's frame; NaN for its own.</summary>
	public static double GetHeight(Control view) => view.GetValue(HeightProperty);

	/// <summary>Sets the height <paramref name="view"/> has in an overlay's frame.</summary>
	public static void SetHeight(Control view, double value) => view.SetValue(HeightProperty, value);
}
