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
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices.JavaScript;
using Avalonia.Threading;
using NLog;
using WaveDudeNext.App.Platform;

namespace WaveDudeNext.Browser;

/// <summary>
/// The browser's own drag as the application's drag source (<see cref="IPageDrags"/>; <c>page-drag.js</c> says why
/// and how): what is armed goes to the page's script without waiting, and the script's three messages come back on
/// the thread the page's scripts are called from. The press's is only noted there, since the application's own
/// press, which Avalonia's script sends after it, is handled later on the UI thread and asks for it then; the
/// drag's start is posted to the UI thread ahead of the input that waits there, so the first event of the drag
/// finds the application told, and its end behind that input, so a drop on the application itself is handled
/// while the drag is still known.
/// </summary>
internal sealed partial class PageDrags : IPageDrags
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(PageDrags).FullName!);

	/// <summary>The one instance, which the script's messages go to.</summary>
	private static PageDrags? _instance;

	private string? _armed;
	private volatile bool _pressed;

	private PageDrags()
	{
	}

	/// <inheritdoc/>
	public string? Dragged { get; private set; }

	/// <inheritdoc/>
	public event EventHandler? Started;

	/// <inheritdoc/>
	public event EventHandler? Ended;

	/// <summary>Loads the script, whose listeners are the page's from then on. On the thread the page's scripts are called from.</summary>
	public static async Task<PageDrags> CreateAsync()
	{
		// beside the page, wherever that is served from: the address counts from the runtime's folder
		await JSHost.ImportAsync("page-drag", "../page-drag.js");
		var drags = new PageDrags();
		_instance = drags;
		await JsInit();
		return drags;
	}

	/// <inheritdoc/>
	public void Arm(string? text)
	{
		_armed = text;
		// a press noted for something armed before, and never asked for, is not this one's
		_pressed = false;
		string armed = text ?? "";
		ScriptThread.Post(
			() =>
			{
				JsArm(armed);
				return Task.CompletedTask;
			},
			ex => Logger.Debug(ex, "The page's drag could not be armed"));
	}

	/// <inheritdoc/>
	public bool TakePress()
	{
		bool pressed = _pressed;
		_pressed = false;
		return pressed;
	}

	/// <summary>The script: a press on what is armed was left to the browser. The scripts' thread.</summary>
	[JSExport]
	internal static Task Pressed()
	{
		if (_instance is { } drags)
		{
			drags._pressed = true;
		}
		return Task.CompletedTask;
	}

	/// <summary>The script: the browser has started the drag of what is armed. The scripts' thread.</summary>
	[JSExport]
	internal static Task DragStarted()
	{
		if (_instance is { } drags)
		{
			Dispatcher.UIThread.Post(() =>
			{
				drags.Dragged = drags._armed;
				Logger.Debug("A drag of the application's is the browser's now ({Length} characters of text)", drags.Dragged?.Length ?? 0);
				drags.Started?.Invoke(drags, EventArgs.Empty);
			}, DispatcherPriority.Send);
		}
		return Task.CompletedTask;
	}

	/// <summary>The script: that drag is over. The scripts' thread.</summary>
	[JSExport]
	internal static Task DragEnded()
	{
		if (_instance is { } drags)
		{
			Dispatcher.UIThread.Post(() =>
			{
				drags.Dragged = null;
				Logger.Debug("The browser's drag of the application's has ended");
				drags.Ended?.Invoke(drags, EventArgs.Empty);
			}, DispatcherPriority.Background);
		}
		return Task.CompletedTask;
	}

	[JSImport("init", "page-drag")]
	private static partial Task JsInit();

	[JSImport("arm", "page-drag")]
	private static partial void JsArm(string text);
}
