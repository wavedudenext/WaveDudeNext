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
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WaveDudeNext.App.Dialogs;

/// <summary>
/// One dialog in the <see cref="DialogOverlay"/>: the veil, and the frame with its title strip around the dialog's
/// view (the markup says how it looks). It is to the view what the dialog's window is on a desktop: it tells the view
/// when it is shown and what key was pressed in it, and closes when the view asks or its own close button is
/// pressed. UI thread only.
/// </summary>
public partial class DialogOverlayFrame : UserControl
{
	/// <summary>The dialog's view, the frame's content; the markup binds the title and the size it names.</summary>
	public static readonly StyledProperty<Control?> ViewProperty = AvaloniaProperty.Register<DialogOverlayFrame, Control?>(nameof(View));

	/// <summary>What the frame's own parts take of the overlay's width: the margin and the outline at either side.</summary>
	private const double ChromeAcross = 2 * (24 + 1);

	/// <summary>What they take of its height: the margin and the outline above and below, and the title strip.</summary>
	private const double ChromeDown = 2 * (24 + 1) + 28;

	private bool _closed;

	/// <summary>The parameterless constructor the XAML loader needs; <see cref="DialogOverlay.ShowAsync"/> makes the real frame.</summary>
	public DialogOverlayFrame()
	{
		InitializeComponent();
		closeButton.Click += (_, _) => Close();
	}

	/// <summary>The frame around <paramref name="view"/>, which is an <see cref="IDialogView"/>.</summary>
	internal DialogOverlayFrame(Control view) : this()
	{
		var dialog = (IDialogView)view;
		View = view;
		dialog.CloseRequested += (_, _) => Close();
		AddHandler(KeyDownEvent, (_, e) => dialog.FrameKeyDown(e), RoutingStrategies.Tunnel);
		Loaded += (_, _) =>
		{
			dialog.Opened();
			if (!IsKeyboardFocusWithin)
			{
				// a view that took no focus: the keys still belong to the dialog, not to what lies under it
				Focus();
			}
		};
	}

	/// <summary>The dialog's view.</summary>
	public Control? View
	{
		get => GetValue(ViewProperty);
		set => SetValue(ViewProperty, value);
	}

	/// <summary>The dialog has closed, by the view's request or the close button; raised once.</summary>
	public event EventHandler? Closed;

	/// <inheritdoc />
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == BoundsProperty || change.Property == ViewProperty)
		{
			FitToPage();
		}
	}

	/// <summary>
	/// The frame never reaches past the page: the view is no wider than the page leaves; a view with a height of its
	/// own (the manual) is no higher either and so shrinks with the page, while one as high as its content keeps that
	/// height and scrolls in the frame where the page is lower.
	/// </summary>
	private void FitToPage()
	{
		double across = Math.Max(0, Bounds.Width - ChromeAcross), down = Math.Max(0, Bounds.Height - ChromeDown);
		host.MaxWidth = across;
		host.MaxHeight = View != null && !double.IsNaN(DialogFrame.GetHeight(View)) ? down : double.PositiveInfinity;
		scroll.MaxHeight = down;
	}

	private void Close()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		Closed?.Invoke(this, EventArgs.Empty);
	}
}
