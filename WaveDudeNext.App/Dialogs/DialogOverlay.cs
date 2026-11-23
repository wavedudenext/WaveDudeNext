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

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NLog;

namespace WaveDudeNext.App.Dialogs;

/// <summary>
/// Where the dialogs are shown in a head without windows (the browser's, 2026-11-15, the user's design): a layer over
/// the main view, empty and out of the pointer's way until a dialog is asked for, then holding one
/// <see cref="DialogOverlayFrame"/> per open dialog, the newest in front, each over a veil of its own that takes the
/// pointer. A dialog shown here is modal as its window is on a desktop: the main view binds its content's
/// <c>IsEnabled</c> to <see cref="IsOpen"/> and takes its key bindings off the top level meanwhile (they are tried
/// before the focused control sees a key, Avalonia's <c>KeyboardDevice</c>), Tab cycles inside the frame, and the
/// keyboard focus goes back to where it was when the dialog has gone. A second dialog over an open one (an error
/// while the manual is read) lies on top and is answered first. UI thread only.
/// </summary>
public sealed class DialogOverlay : Panel
{
	/// <summary>Whether a dialog is open in the overlay.</summary>
	public static readonly DirectProperty<DialogOverlay, bool> IsOpenProperty =
		AvaloniaProperty.RegisterDirect<DialogOverlay, bool>(nameof(IsOpen), overlay => overlay.IsOpen);

	private static readonly Logger Logger = LogManager.GetLogger(typeof(DialogOverlay).FullName!);

	/// <summary>The overlay of each top level that has one, so a dialog asked for by any control of it is shown there.</summary>
	private static readonly ConditionalWeakTable<TopLevel, DialogOverlay> Overlays = [];

	private TopLevel? _topLevel;
	private bool _isOpen;

	/// <summary>Whether a dialog is open in the overlay: the application under it is out of reach then.</summary>
	public bool IsOpen
	{
		get => _isOpen;
		private set => SetAndRaise(IsOpenProperty, ref _isOpen, value);
	}

	/// <summary>The overlay of the top level <paramref name="anchor"/> is in; null where that has none, or the control is in no top level yet.</summary>
	public static DialogOverlay? Of(Control anchor) =>
		TopLevel.GetTopLevel(anchor) is { } top && Overlays.TryGetValue(top, out DialogOverlay? overlay) ? overlay : null;

	/// <summary>
	/// Shows <paramref name="view"/> in a frame over the main view, modally, in front of a dialog already open. The task
	/// completes when the dialog has closed, by the view's own request or the frame's close button, the frame gone and
	/// the keyboard focus back where it was.
	/// </summary>
	public Task ShowAsync<TView>(TView view) where TView : Control, IDialogView
	{
		var closed = new TaskCompletionSource();
		// where the keyboard was: the main view's content loses the focus as it is disabled under the first dialog
		IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
		var frame = new DialogOverlayFrame(view);
		frame.Closed += (_, _) =>
		{
			Children.Remove(frame);
			frame.View = null;
			IsOpen = Children.Count > 0;
			// Back to where the keyboard was, but only when the input that closed the dialog has passed: the letter
			// that pressed a button (Y for Yes) is still on its way as text, since a page cannot keep a key it has
			// handled from being typed, and with the focus back at once it landed in the text box the focus returned
			// to (seen 2026-11-15: a source renamed "yySource 2" by two answers). Until then nothing has the focus,
			// and the text goes nowhere. Not when another dialog has opened meanwhile: its content is disabled then.
			Dispatcher.UIThread.Post(() =>
			{
				if (focused is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true })
				{
					focused.Focus();
				}
			}, DispatcherPriority.Background);
			Logger.Debug("Dialog closed: {View}", view.GetType().Name);
			closed.SetResult();
		};
		Logger.Debug("Dialog over the main view: {View}", view.GetType().Name);
		Children.Add(frame);
		IsOpen = true;
		return closed.Task;
	}

	/// <inheritdoc />
	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (TopLevel.GetTopLevel(this) is { } top)
		{
			_topLevel = top;
			Overlays.AddOrUpdate(top, this);
		}
	}

	/// <inheritdoc />
	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		if (_topLevel is { } top && Overlays.TryGetValue(top, out DialogOverlay? overlay) && ReferenceEquals(overlay, this))
		{
			Overlays.Remove(top);
		}
		_topLevel = null;
	}
}
