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

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLog;
using WaveDudeNext.App.Dialogs;

namespace WaveDudeNext.App.Help;

/// <summary>
/// The manual (Help > Manual, F1): MANUAL.md as the build converts it (<see cref="HelpDocument"/>) beside a
/// contents tree of its chapters and their sections, every branch open; the title, the one level-1 heading, is no row.
/// It opens with the text at its top and the first chapter's row taken; taking a row goes to its heading, which then
/// stands at the top of the text; a link to "#anchor" goes to that heading, a web link opens in the browser. The
/// dialog's content (<see cref="IDialogView"/>, 2026-11-15): <see cref="Show"/> puts it into a
/// <see cref="ManualWindow"/> on a desktop, not modal but owned by the main window (the user's choice), and over the
/// main view where there are no windows (the browser head), where it is in the application's way until it is closed.
/// Upstream reloaded its WPF document when the UI zoom changed the text formatting mode; Avalonia has one text
/// renderer, so there is nothing to reload. UI thread only.
/// </summary>
public partial class ManualView : UserControl, IDialogView
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(ManualView).FullName!);
	private readonly HelpDocument _document;
	private readonly long _start;
	private readonly double _read;
	/// <summary>Set while the first row is taken on opening, so the text stays at its top instead of going to the heading.</summary>
	private bool _opening;

	/// <summary>Loads the manual and lists its headings; the dialog opens with the text at its top and the first row taken, the tree focused.</summary>
	public ManualView()
	{
		_start = Stopwatch.GetTimestamp();
		_document = HelpDocument.Load(HelpDocument.Manual);
		_read = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
		_document.LinkActivated += OnLink;
		IReadOnlyList<ManualHeading> all = _document.Contents();
		// the chapters are the rows: the title, the document's one level-1 heading, would be a lone root that indents
		// every other row, and the frame's title names the manual already
		Headings = all is [{ Level: 1 } title] ? title.Children : all;
		InitializeComponent();
		documentView.Content = _document.Root;
		contents.ItemsSource = Headings;
		contents.SelectionChanged += OnContentsSelected;
		contents.AddHandler(KeyDownEvent, OnContentsKeyDown, RoutingStrategies.Bubble);
		contents.AddHandler(PointerReleasedEvent, OnContentsPointerReleased, RoutingStrategies.Bubble);
	}

	/// <summary>The contents tree's rows: the manual's chapters, each with its sections under it.</summary>
	public IReadOnlyList<ManualHeading> Headings { get; }

	/// <summary>The manual's document, for the tests and the view's own navigation.</summary>
	public HelpDocument Document => _document;

	/// <inheritdoc />
	public event EventHandler? CloseRequested;

	/// <summary>
	/// Shows the manual for <paramref name="anchor"/>, a control of the main view: in its window, owned by the
	/// anchor's, or that window brought forward when it is open already; over the main view where the anchor is in
	/// no window, until it is closed.
	/// </summary>
	public static void Show(Control anchor)
	{
		if (TopLevel.GetTopLevel(anchor) is Window owner)
		{
			ManualWindow.ShowOrActivate(owner);
		}
		else if (DialogOverlay.Of(anchor) is { } overlay)
		{
			_ = overlay.ShowAsync(new ManualView());
		}
		else
		{
			Logger.Warn("The manual cannot be shown: its view is not on screen");
		}
	}

	/// <summary>The frame shows the manual: the first row is taken once the tree's rows exist.</summary>
	public void Opened()
	{
		Dispatcher.UIThread.Post(TakeFirstRow, DispatcherPriority.Loaded);
		Logger.Debug("Manual open: {Headings} headings, {Figures} figures, the document read in {Read:0} ms, on screen after {Shown:0} ms",
			_document.Headings.Count, _document.FigureCount, _read, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
	}

	/// <summary>Esc closes even while the text has the focus.</summary>
	public void FrameKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			CloseRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>The first row taken without going there: the text opens at its top, the title in view above the first chapter.</summary>
	private void TakeFirstRow()
	{
		if (Headings.Count == 0)
		{
			return;
		}
		_opening = true;
		contents.SelectedItem = Headings[0];
		_opening = false;
		if (contents.ContainerFromItem(Headings[0]) is TreeViewItem first)
		{
			first.Focus();
		}
		else
		{
			contents.Focus();
		}
	}

	private void OnContentsSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (!_opening && contents.SelectedItem is ManualHeading heading)
		{
			GoTo(heading);
		}
	}

	/// <summary>Enter on a row goes to its heading again and moves the focus into the text, to read on with the keys.</summary>
	private void OnContentsKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && contents.SelectedItem is ManualHeading heading)
		{
			GoTo(heading);
			documentView.Focus();
			e.Handled = true;
		}
	}

	/// <summary>
	/// A click on the row already taken goes to its heading again (a click on another row selects it, which goes there);
	/// the innermost row takes the click, the expander's chevron is no part of it.
	/// </summary>
	private void OnContentsPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (e.InitialPressMouseButton != MouseButton.Left)
		{
			return;
		}
		TreeViewItem? row = null;
		for (Visual? element = e.Source as Visual; element != null && element != contents; element = element.GetVisualParent())
		{
			if (element is ToggleButton)
			{
				return;
			}
			if (element is TreeViewItem item)
			{
				row = item;
				break;
			}
		}
		if (row is { IsSelected: true, DataContext: ManualHeading heading } && ReferenceEquals(contents.SelectedItem, heading))
		{
			GoTo(heading);
		}
	}

	/// <summary>
	/// Scrolls the text so the heading stands the page's padding below the top, the end of the section before it out of
	/// sight (its own margin above would show it); offsets go to whole pixels, so a screenshot that fits the column
	/// stays drawn 1:1.
	/// </summary>
	public void GoTo(ManualHeading heading)
	{
		Point? top = heading.Block.TranslatePoint(new Point(0, 0), documentView);
		if (top == null)
		{
			heading.Block.BringIntoView();
			return;
		}
		double padding = _document.Root.Margin.Top;
		double offset = documentView.Offset.Y + top.Value.Y - padding;
		double most = Math.Max(0, documentView.Extent.Height - documentView.Viewport.Height);
		documentView.Offset = new Vector(documentView.Offset.X, Math.Round(Math.Clamp(offset, 0, most)));
	}

	/// <summary>A link: "#anchor" goes to that heading, anything else opens in the browser.</summary>
	private void OnLink(object? sender, DocumentLinkEventArgs e)
	{
		if (e.Target.StartsWith('#'))
		{
			if (_document.Find(e.Target[1..]) is { } heading)
			{
				GoTo(heading);
			}
			else
			{
				Logger.Debug("Manual: no heading for the link {Target}", e.Target);
			}
			return;
		}
		AboutView.OpenInBrowser(this, e.Target);
	}
}
