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

using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Threading;
using WaveDudeNext.App.Theming;
using WaveDudeNext.Engine;
using WaveDudeNext.Runs;
using WaveDudeNext.UI;

[assembly: SupportedOSPlatform("browser")]

namespace WaveDudeNext.Browser;

/// <summary>
/// The browser head's entry point: the logging first, then the Avalonia head's application
/// (<see cref="App.App"/>, of the WaveDudeNext.App assembly) with the browser's single view in the page's element
/// <c>out</c>. Main returns once the application is set up; the runtime goes on with the page.
/// </summary>
internal static partial class Program
{
	/// <summary>The key of the font collection this head registers for the faces it carries.</summary>
	private const string FontCollectionKey = "fonts:Roboto";

	/// <summary>The folder of those faces among this assembly's Avalonia resources (the project file says which and why).</summary>
	private const string FontSource = "avares://WaveDudeNext.Browser/Assets/Fonts";

	/// <summary>The font this head carries, as Avalonia names it: a browser gives a page no system fonts.</summary>
	private const string FontFamilyName = FontCollectionKey + "#Roboto";

	/// <summary>The system's own font where the browser hands it over (<see cref="SystemFont"/>); null where it does not.</summary>
	private static SystemFont? _systemFont;

	/// <summary>The context of the thread the page's scripts are called from, <c>Main</c>'s, for what another thread has to tell the page.</summary>
	private static SynchronizationContext? _scripts;

	private static async Task Main(string[] args)
	{
		// the thread the page's scripts can be called from: the console target posts its lines here, the GPU engine its
		// calls to the card, the frame cache its file calls
		SynchronizationContext? scripts = SynchronizationContext.Current;
		// the page's loading mark is told how far the start is (main.js, waveDudeStage): the program runs
		ShowStage(1);
		App.App.SetupLogging(new BrowserConsoleTarget(scripts));
		// what a desktop head's first line says with its executable's path: here the page's address, which main.js
		// hands over as the one argument
		if (args is [{ Length: > 0 } address, ..])
		{
			App.App.Logger.Info("Application: '{Address}'", address);
		}
		// an error nothing handled leaves a notice over the page (main.js): the page itself stays, and without the
		// notice it just no longer answers. Posted, not waited for: the failing thread is on its way out
		if (scripts != null)
		{
			App.App.Stopped = message => scripts.Post(static state => ShowStopped((string)state!), message);
			// the page's loading mark goes when the program's first picture is on the screen (index.html, main.js)
			App.App.Shown = () => scripts.Post(static _ => HideLoadingMark(), null);
		}
		// the program's memory in its two parts, before anything large is allocated: the heap below, a run's frames
		// above (FrameArena says why)
		if (FrameArena.TryCreate(FrameMemory.BrowserReserve, out string layout) is { } frames)
		{
			FrameMemory.Allocator = frames;
		}
		App.App.Logger.Info("Memory: {Layout}", layout);
		if (scripts != null)
		{
			ScriptThread.Use(scripts);
			// what the page's scripts are asked for at the start is asked all at once (2026-11-22): each is a script
			// fetched from the server and a question or two, none needs another's answer, and one after the other they
			// were as many trips to the server ahead of the application's own start
			long asked = Stopwatch.GetTimestamp();
			Task<LocalStorageSettings> settings = LocalStorageSettings.CreateAsync();
			Task<SystemFont?> systemFont = SystemFont.CreateAsync();
			// the user settings are kept in the browser's local storage (2026-11-14, the user's choice), where the desktop
			// heads have a file; before anything asks for a setting, since the settings are read at their first use
			try
			{
				SettingsStore.Current = await settings;
			}
			catch (Exception ex)
			{
				App.App.Logger.Warn(ex, "The browser's local storage cannot be reached: the settings last until the page is left");
			}
			// the storage the browser manages for the page stands in for the cache folder on a drive, which a page does
			// not have: the frames of a run that do not fit the memory go there
			FrameCache.Storage = new OpfsFrameStorage();
			// the system's own font in place of the one this head carries, where the browser hands a page the installed
			// fonts and the user has allowed it (the View menu's "Use the system's font", 2026-11-14, the user's design);
			// where the permission was given on an earlier visit the page has read the faces by now
			_systemFont = await systemFont;
			App.App.SystemFont = _systemFont;
			App.App.Logger.Debug("The page's scripts had answered {Elapsed:0} ms after they were asked", Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
		}
		// the browser's GPU engine stands in for the OpenCL one, which a browser does not have; where the browser has
		// no WebGPU either, the factory says why and gives the CPU engine
		WaveEngineFactory.PlatformGpu = WebGpuWaveSimulator.TryCreate;
		try
		{
			// the loading mark: the application is being set up; and, from the builder's AfterSetup, its view is made
			ShowStage(2);
			_scripts = scripts;
			await BuildAvaloniaApp().StartBrowserAppAsync("out");
		}
		catch (Exception ex)
		{
			// the page would stay empty without a word: the browser's console is this head's log
			App.App.Logger.Fatal(ex, "The application could not start");
			throw;
		}
	}

	/// <summary>
	/// Avalonia's configuration: the application class of the Avalonia head and the one font this head carries, as the
	/// default family. That serves both kinds of text: the controls', since the theme names its own font first and the
	/// default after it, and its own is not there (Fluent's <c>ContentControlThemeFontFamily</c> is
	/// <c>fonts:Inter#Inter, $Default</c>, read at 12.1.5; the desktop head gets the platform's font the same way), and
	/// what the application draws itself (the names beside the handles on the pictures, the timeline's numbers, the
	/// graph boxes), which asks for the default and without this got the renderer's built-in monospaced face
	/// (2026-11-12, the user's report). The faces are hinted, and the hinting is asked for without anything set here:
	/// Avalonia's Skia backend takes an unspecified hinting mode as full hinting by the font's own instructions
	/// (<c>GlyphRunImpl.CreateFont</c>, read at 12.1.5). Where the system's own font came with the page's load it takes
	/// the carried one's place before the view exists (<see cref="SystemFont.UseAtStart"/>; the builder runs these
	/// after the application's own initialisation and before its view is made, in the order given here). The glyphs
	/// are placed on whole pixels as in the desktop head (<see cref="WholePixelShaper"/>, 2026-11-21, the user's
	/// choice: without it this head's stems lay across two columns); it is bound once the platform's services are up
	/// and before any text is shaped, and where it cannot be, the head runs without it and says so in the log.
	/// </summary>
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
		.ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(
			new Uri(FontCollectionKey, UriKind.Absolute), new Uri(FontSource, UriKind.Absolute))))
		.AfterPlatformServicesSetup(_ => WholePixelShaper.Install())
		.AfterSetup(_ =>
		{
			_systemFont?.UseAtStart();
			// the loading mark's last stage, told at once where this is the scripts' own thread: a message posted to
			// it would wait until the view is made, which is the stage it is to announce
			if (SynchronizationContext.Current == _scripts)
			{
				ShowStage(3);
			}
			else
			{
				_scripts?.Post(static _ => ShowStage(3), null);
			}
		})
		.With(new FontManagerOptions { DefaultFamilyName = FontFamilyName });

	/// <summary>
	/// The page saw an input event (main.js calls this after every one): the UI thread is woken to take it. On the
	/// threaded runtime Avalonia's UI thread sleeps until a timer of its own is due or a job is posted to it, and the
	/// browser's input is queued for it without waking it (Avalonia 12.1.5), so a hover or a click showed 0.4 to 1 s
	/// late, whenever something else woke the thread, and never once nothing did, as after the page had lost the focus
	/// (2026-11-12, the user's reports: "slow as molasses", and frozen on coming back to the window). The empty job is
	/// that wake-up, at the priority of input.
	/// </summary>
	[JSExport]
	internal static Task Wake()
	{
		Dispatcher.UIThread.Post(static () => { }, DispatcherPriority.Input);
		return Task.CompletedTask;
	}

	/// <summary>main.js: the notice over the page that the program has stopped, with the error's words; the first call's stays.</summary>
	[JSImport("globalThis.waveDudeStopped")]
	private static partial void ShowStopped(string message);

	/// <summary>main.js: the page's loading mark, shown from the page's first moment, is taken away.</summary>
	[JSImport("globalThis.waveDudeStarted")]
	private static partial void HideLoadingMark();

	/// <summary>
	/// main.js: the loading mark's bar goes on to the end of a stage of the start (2026-11-22, the user's report of
	/// a long gap after "Starting…", the bar having stood full for the seconds the program takes): 1 when Main
	/// runs, 2 when the application is being set up, 3 when it is set up and its view is being made. The scripts'
	/// thread.
	/// </summary>
	[JSImport("globalThis.waveDudeStage")]
	private static partial void ShowStage(int stage);

	/// <summary>
	/// An error of the page's own scripts that nothing caught, or a promise nothing took the rejection of (main.js
	/// hears both): into the application's log as an error, so it shows in the status bar and the Messages pane
	/// and not in the browser's console alone. Such an error need not end the program, so it is not the notice;
	/// where this call itself fails, the program is gone, and main.js puts the notice up.
	/// </summary>
	[JSExport]
	internal static Task PageError(string message)
	{
		// the first line, what the error says: the status bar shows the message, and the lines of the script's stack
		// under it are in the browser's console, which prints an uncaught error itself
		App.App.Logger.Error("An error in the page's scripts: {Message}", message.Split('\n')[0].Trim());
		return Task.CompletedTask;
	}

	/// <summary>
	/// The harness's hook for the crash notice, which nothing else can bring about on purpose: an error nothing
	/// handles on the UI thread (<c>ui</c>), on a thread of its own (<c>thread</c>), or the runtime's own end
	/// (<c>fail</c>). Called from the page, by hand or through the browser's debugging port.
	/// </summary>
	[JSExport]
	internal static Task Crash(string kind)
	{
		switch (kind)
		{
			case "ui":
				Dispatcher.UIThread.Post(static () => throw new InvalidOperationException("An error on the UI thread, asked for through Program.Crash."));
				break;
			case "thread":
				// the analyser knows the browser as a platform without threads; this head runs on the threaded runtime
#pragma warning disable CA1416
				new Thread(static () => throw new InvalidOperationException("An error on a thread of its own, asked for through Program.Crash.")).Start();
#pragma warning restore CA1416
				break;
			case "fail":
				Environment.FailFast("The runtime's end, asked for through Program.Crash.");
				break;
		}
		return Task.CompletedTask;
	}
}
