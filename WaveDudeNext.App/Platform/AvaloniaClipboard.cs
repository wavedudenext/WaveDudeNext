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

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using NLog;
using WaveDudeNext.UI;

namespace WaveDudeNext.App.Platform;

/// <summary>
/// The Avalonia head's clipboard for the models of <c>WaveDudeNext.UI</c> (<see cref="IClipboard"/>). Avalonia's clipboard
/// answers later, and the interface answers at once, as it foresees: a read starts an asynchronous read and reports
/// "nothing known" until that has landed, after which the text read is answered for as long as the clipboard's change
/// count stays (Windows' clipboard sequence number). A write is answered when the platform has taken or refused it
/// (<see cref="SetTextAsync"/>, which Cut waits for); <see cref="TrySetText"/> starts one and does not wait.
/// <para>
/// Without a change count (0, anything but Windows) what was read last is the answer for now and every question
/// reads again behind it, <see cref="Changed"/> telling the commands when that read found something else; and a
/// paste reads first (<see cref="ReadNowAsync"/>), so what is pasted is what the clipboard holds at that moment
/// (2026-11-21: until then the first read's text was kept for good there, and a copy made in another program or
/// tab was never seen). In a browser no question reads at all (<see cref="AnswersQuestions"/>).
/// </para>
/// UI thread.
/// </summary>
/// <param name="anchor">A control of the view; the clipboard is its top level's (the window's, or the browser's root), looked up at each use.</param>
public sealed class AvaloniaClipboard(Control anchor) : IClipboard
{
	private static readonly ILogger Logger = LogManager.GetLogger(typeof(AvaloniaClipboard).FullName!);

	private (uint ChangeCount, string? Text)? _read;
	private uint _reading;
	private bool _readInFlight;

	/// <summary>A read behind a question has landed with another answer than the one given so far: the commands that asked are to ask again.</summary>
	public event EventHandler? Changed;

	/// <inheritdoc/>
	public uint ChangeCount => OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;

	/// <summary>
	/// Whether the clipboard may be read whenever a command asks what it holds. Not in a browser (2026-11-21): there a
	/// read asks the user for a permission or wants a gesture of his, in some browsers every time, so a menu that
	/// opens must not read (the head had asked for the permission at its first question, out of nowhere). The
	/// clipboard is read at the paste alone there, and Paste is offered without knowing what it holds.
	/// </summary>
	public bool AnswersQuestions => !OperatingSystem.IsBrowser();

	/// <inheritdoc/>
	public bool TrySetText(string format, string text)
	{
		// started and not waited for: the answer here is that there is a clipboard, a refusal is logged when it comes
		_ = SetTextAsync(format, text);
		return TopLevel.GetTopLevel(anchor)?.Clipboard != null;
	}

	/// <summary>
	/// Puts the text on the clipboard and answers when the platform has taken it or refused it (2026-11-22). A
	/// refusal is a warning, in words: until then it had left the write's task as an error nobody looked at, which
	/// reached the status bar as a fatal one, and a Cut had removed what the clipboard never took. A browser's
	/// clipboard holds the plain text alone (Avalonia leaves an application's own format out there), which is what
	/// a paste reads back.
	/// </summary>
	public async Task<bool> SetTextAsync(string format, string text)
	{
		Avalonia.Input.Platform.IClipboard? clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;
		if (clipboard == null)
		{
			Logger.Warn("No clipboard on this platform; nothing was copied");
			return false;
		}
		var transfer = new DataTransfer();
		var item = new DataTransferItem();
		item.Set(DataFormat.CreateStringApplicationFormat(format), text);
		item.SetText(text);
		transfer.Add(item);
		_read = null;
		try
		{
			await clipboard.SetDataAsync(transfer);
			return true;
		}
		catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
		{
			Logger.Warn(ex, "The clipboard is busy; nothing was copied");
			return false;
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException || OperatingSystem.IsBrowser())
		{
			// a browser refuses a page the clipboard without the user's permission, and while the page is not the one
			// in front; whatever else it refuses with is its own kind of error
			Logger.Warn("The clipboard may not be written now: nothing was copied ({0})", ex.Message);
			return false;
		}
	}

	/// <inheritdoc/>
	public bool TryGetText(string format, out string? text)
	{
		uint count = ChangeCount;
		bool known = _read is { } read && (count == 0 || read.ChangeCount == count);
		text = known ? _read!.Value.Text : null;
		// what was read is good while the change count stays; without a count it is the answer for now, and the
		// clipboard is read again behind it. Never for a question in a browser
		if ((!known || count == 0) && !_readInFlight && AnswersQuestions)
		{
			_reading = count;
			_readInFlight = true;
			_ = ReadAsync(format);
		}
		return known;
	}

	/// <summary>
	/// Reads the clipboard now and keeps what it holds as the answer to the questions that follow: for a paste where
	/// the clipboard's changes are not counted, so the answer kept may be an old one, or none at all (a browser).
	/// </summary>
	/// <param name="format">The application's own format, looked for first; else the plain text.</param>
	/// <returns>False when the clipboard could not be read: nothing is known then.</returns>
	public async Task<bool> ReadNowAsync(string format)
	{
		uint count = ChangeCount;
		(bool read, string? text) = await ReadOnceAsync(format);
		_read = read ? (count, text) : null;
		return read;
	}

	/// <summary>The read behind a question: its outcome is the answer from now on, and <see cref="Changed"/> says so where it is another one.</summary>
	private async Task ReadAsync(string format)
	{
		(bool read, string? text) = await ReadOnceAsync(format);
		(uint ChangeCount, string? Text)? before = _read;
		_read = read ? (_reading, text) : null;
		_readInFlight = false;
		if (read && (before is not { } known || known.Text != text))
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>One read of the clipboard: whether it could be read, and the text it holds under the format, else its plain text, else null.</summary>
	private async Task<(bool Read, string? Text)> ReadOnceAsync(string format)
	{
		try
		{
			if (TopLevel.GetTopLevel(anchor)?.Clipboard is { } clipboard && await clipboard.TryGetDataAsync() is { } data)
			{
				return (true, await data.TryGetValueAsync(DataFormat.CreateStringApplicationFormat(format)) ?? await data.TryGetTextAsync());
			}
			return (true, null);
		}
		catch (ExternalException ex)
		{
			// another program holds the clipboard open (CLIPBRD_E_CANT_OPEN): nothing is known, and the next question reads
			// again, as the interface says and the WPF head does (2026-11-11: the refusal had been kept as "holds nothing"
			// until the clipboard's next change, and logged as a warning with its stack trace)
			Logger.Debug("The clipboard is held by another program; the next question reads again ({0})", ex.Message);
			return (false, null);
		}
		catch (UnauthorizedAccessException ex)
		{
			// a browser refuses a read without the user's permission, and with it while the page is not focused: nothing
			// is known (2026-11-12, the browser head: the refusal had gone unobserved and reached the status bar as a
			// fatal error)
			Logger.Debug("The clipboard may not be read now ({0})", ex.Message);
			return (false, null);
		}
		catch (InvalidOperationException)
		{
			// what the clipboard holds cannot be had as text after all: as good as none
			return (true, null);
		}
		catch (Exception ex) when (OperatingSystem.IsBrowser())
		{
			// whatever else a browser refuses a read with (its errors are its own kind): nothing is known
			Logger.Debug("The browser did not hand over the clipboard ({0})", ex.Message);
			return (false, null);
		}
	}

	[DllImport("user32.dll")]
	private static extern uint GetClipboardSequenceNumber();
}
