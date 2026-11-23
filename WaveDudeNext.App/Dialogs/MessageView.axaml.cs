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
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using NLog;

namespace WaveDudeNext.App.Dialogs;

/// <summary>What a message reports: an error (red disc) or a question (accent disc).</summary>
public enum MessageKind
{
	/// <summary>Something failed; the text names it.</summary>
	Error,
	/// <summary>A decision the user has to take.</summary>
	Question,
}

/// <summary>The button sets a message offers.</summary>
public enum MessageButtons
{
	/// <summary>OK alone; Enter, Esc and the close box all give <see cref="MessageAnswer.Ok"/>.</summary>
	Ok,
	/// <summary>Yes (default) and No (Esc and the close box).</summary>
	YesNo,
	/// <summary>Save (default), Don't save, and Cancel (Esc and the close box).</summary>
	SaveDiscardCancel,
}

/// <summary>The button a message was closed with.</summary>
public enum MessageAnswer
{
	/// <summary>Cancel, Esc or the close box of a <see cref="MessageButtons.SaveDiscardCancel"/> message.</summary>
	Cancel,
	/// <summary>OK, or any way of closing a <see cref="MessageButtons.Ok"/> message.</summary>
	Ok,
	/// <summary>Yes.</summary>
	Yes,
	/// <summary>No, Esc or the close box of a <see cref="MessageButtons.YesNo"/> message.</summary>
	No,
	/// <summary>Save.</summary>
	Save,
	/// <summary>Don't save.</summary>
	Discard,
}

/// <summary>
/// The application's message box, as upstream's: in the theme's colours, the text wrapped beside an error or question
/// disc, and the buttons of a <see cref="MessageButtons"/> set labelled from the string tables (<c>Dialog*</c> keys), so
/// they speak the app's language, not the desktop's. Enter takes the default button, Esc the cancel button, the
/// underlined letters work without Alt; the buttons carry <c>Message&lt;Answer&gt;</c> automation ids. The dialog's
/// content (<see cref="IDialogView"/>, 2026-11-15): <see cref="Show"/> puts it into a modal <see cref="MessageWindow"/>
/// owned by the caller's window on a desktop, and over the main view where there are no windows (the browser head);
/// either way it answers later, so <see cref="Show"/> returns a task the caller awaits. Upstream's system sounds are
/// not played (Avalonia has no portable equivalent). UI thread only.
/// </summary>
public partial class MessageView : UserControl, IDialogView
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(MessageView).FullName!);
	private Button? _defaultButton;

	/// <summary>An empty message, as the XAML loader and the window's markup make it; <see cref="Set"/> gives it its text and buttons.</summary>
	public MessageView()
	{
		InitializeComponent();
	}

	/// <summary>The answer the dialog closes with: the set's cancel answer until a button is pressed.</summary>
	public MessageAnswer Answer { get; private set; }

	/// <summary>
	/// A scripted answer for the headless tests: while set, <see cref="Show"/> completes with it at once and shows
	/// nothing (the main window's tests confirm or discard edits this way); null, the default, shows the dialog.
	/// </summary>
	public static MessageAnswer? NextAnswer { get; set; }

	/// <inheritdoc />
	public event EventHandler? CloseRequested;

	/// <summary>
	/// Shows <paramref name="text"/> modally for <paramref name="anchor"/>, a control of the view the message belongs
	/// to: in a window over the anchor's, or over the main view where the anchor is in no window. Completes with the
	/// button the dialog was closed with (the set's cancel answer for Esc or the close box, and where the message
	/// cannot be shown at all, the anchor not being on screen yet, which the log says). UI thread only.
	/// </summary>
	public static async Task<MessageAnswer> Show(Control anchor, string text, MessageKind kind, MessageButtons buttons)
	{
		Logger.Debug("Message ({Kind}, {Buttons}): {Text}", kind, buttons, text.ReplaceLineEndings(" "));
		if (NextAnswer is { } scripted)
		{
			Logger.Debug("Message answered by the test script: {Answer}", scripted);
			return scripted;
		}
		MessageView view;
		if (TopLevel.GetTopLevel(anchor) is Window owner)
		{
			var window = new MessageWindow(text, kind, buttons);
			view = window.View;
			await window.ShowDialog(owner);
		}
		else
		{
			view = new MessageView();
			view.Set(text, kind, buttons);
			if (DialogOverlay.Of(anchor) is { } overlay)
			{
				await overlay.ShowAsync(view);
			}
			else
			{
				Logger.Warn("The message cannot be shown, its view is not on screen: it is taken as answered {Answer}", view.Answer);
			}
		}
		Logger.Debug("Message answered: {Answer}", view.Answer);
		return view.Answer;
	}

	/// <summary>The message's text, its kind's disc and its buttons, replacing what it had.</summary>
	public void Set(string text, MessageKind kind, MessageButtons buttons)
	{
		messageText.Text = text;
		bool error = kind == MessageKind.Error;
		iconDisc[!Shape.FillProperty] = new DynamicResourceExtension(error ? "StatusBarErrorBackground" : "Accent");
		iconGlyph[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(error ? "StatusBarErrorForeground" : "StatusBarForeground");
		iconGlyph.Text = error ? "!" : "?";
		buttonRow.Children.Clear();
		_defaultButton = null;
		// the answer for the close box: what the cancel button of the set would give
		switch (buttons)
		{
			case MessageButtons.Ok:
				Answer = MessageAnswer.Ok;
				AddButton("DialogOk", MessageAnswer.Ok, isDefault: true, isCancel: true);
				break;
			case MessageButtons.YesNo:
				Answer = MessageAnswer.No;
				AddButton("DialogYes", MessageAnswer.Yes, isDefault: true, isCancel: false);
				AddButton("DialogNo", MessageAnswer.No, isDefault: false, isCancel: true);
				break;
			default:
				Answer = MessageAnswer.Cancel;
				AddButton("DialogSave", MessageAnswer.Save, isDefault: true, isCancel: false);
				AddButton("DialogDiscard", MessageAnswer.Discard, isDefault: false, isCancel: false);
				AddButton("DialogCancel", MessageAnswer.Cancel, isDefault: false, isCancel: true);
				break;
		}
	}

	/// <inheritdoc />
	public void Opened() => _defaultButton?.Focus();

	/// <summary>Esc closes with the cancel answer wherever the focus is; a bare letter presses the button it underlines.</summary>
	public void FrameKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			CloseRequested?.Invoke(this, EventArgs.Empty);
		}
		else if (DialogStrings.TryPressAccessLetter(this, e))
		{
			e.Handled = true;
		}
	}

	private void AddButton(string labelKey, MessageAnswer answer, bool isDefault, bool isCancel)
	{
		var button = new Button { MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
		button[!ContentProperty] = new DynamicResourceExtension(labelKey);
		AutomationProperties.SetAutomationId(button, "Message" + answer);
		button.Click += (_, _) =>
		{
			Answer = answer;
			CloseRequested?.Invoke(this, EventArgs.Empty);
		};
		buttonRow.Children.Add(button);
		if (isDefault)
		{
			_defaultButton = button;
		}
	}
}
