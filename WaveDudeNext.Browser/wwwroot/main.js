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

// Starts the .NET runtime and the application's Main, with the page's address as its one argument. The threaded
// runtime needs a cross-origin isolated page: a server that sends the two headers isolates it, and where the server
// sends none the page isolates itself first (isolate, below); where neither works the page says so. The runtime's
// loader is therefore imported only once the page is isolated, further down: the threaded runtime's dotnet.js
// refuses a page that is not at the moment it is loaded ("SharedArrayBuffer is not enabled on this page"), and
// imported at the top of this file it did so before a line of this file had run.

// Where the server does not send the two isolation headers, the page gets them from a service worker of its own
// (isolation-worker.js), through which every file of the site then reaches it (2026-11-14, for GitHub Pages, which
// sends no headers of a site's choosing). Whether a page is isolated is settled when it is loaded, so the page that
// installs the worker gains nothing from it itself: it loads itself again, at once and without the visitor's doing,
// and that load, which comes through the worker, is isolated, as every later visit is at its first load. A page
// loaded past the worker (Ctrl+F5) goes the same way. Under the .NET SDK's host, which Start runs and which sends
// the headers, the page is isolated as it comes and no worker is installed.
//
// That the page is being loaded again for this is noted in its entry of the tab's history, which goes through a
// reload with the entry and which the isolated page clears. Not in the session's storage: what the page that is not
// isolated wrote there, the isolated one neither saw nor removed (Edge 157), so the first visit's note was still
// there for a Ctrl+F5 much later, which took itself for the load that had failed.
//
// Returns only by throwing, with what stands in the way: otherwise the page is on its way out.
const isolating = () => !!(history.state && history.state.waveDudeIsolating);
async function isolate() {
	// the load the worker was to isolate is not isolated either: said, not tried for ever (the next load tries again)
	if (isolating()) {
		history.replaceState(null, '');
		throw new Error('the page is not isolated from other sites, which the program\'s threads need, and loading it again through its service worker has not changed that');
	}
	const needed = 'on this server the page needs a service worker for the program\'s threads, and this browser ';
	if (!globalThis.isSecureContext || !navigator.serviceWorker) {
		throw new Error(needed + 'gives it none (a private window can be the reason)');
	}
	try {
		await navigator.serviceWorker.register('./isolation-worker.js');
	} catch (error) {
		throw new Error(needed + 'did not install it (a private window can be the reason): ' + (error && error.message || error));
	}
	// Active is enough, the worker need not have taken this page: a page loaded past it is never taken, and its
	// next load comes through the worker all the same.
	await navigator.serviceWorker.ready;
	history.replaceState({ waveDudeIsolating: true }, '');
	// The browser may keep this page, left for its reload, as it keeps a page for "back", and show it again: a
	// reload straight after a Ctrl+F5 did (Edge 157, the debugging port's reload: this page restored, waiting
	// below for ever, nothing on the screen). Shown again, it does what it had set out to do.
	globalThis.addEventListener('pageshow', event => { if (event.persisted) { globalThis.location.reload(); } });
	globalThis.location.reload();
	await new Promise(() => { });
}

// A picked file is written through Avalonia.Browser's own script, which on the threaded runtime hands the browser's
// file stream .NET's view of the bytes instead of the bytes (12.1.5, StreamHelper.write in avalonia.js: where the
// program's memory is shared it copies the view into an array, `t.copyTo(a, r)`, and then writes the view, `s = t`).
// The stream refuses that value with a TypeError, so nothing could be saved or exported in this head (2026-11-15).
// Until Avalonia writes the array it made, the stream's write does: a value that is such a view is copied into an
// array first. The program writes whole arrays alone (PickedFiles.cs), so the view's length is what is to be written.
if (globalThis.FileSystemWritableFileStream) {
	const write = FileSystemWritableFileStream.prototype.write;
	FileSystemWritableFileStream.prototype.write = function (data) {
		if (data && typeof data.copyTo === 'function' && typeof data.byteLength === 'number' && !ArrayBuffer.isView(data)) {
			const bytes = new Uint8Array(data.byteLength);
			data.copyTo(bytes);
			data = bytes;
		}
		return write.call(this, data);
	};
}

// The application's log in the console (BrowserConsoleTarget calls this for every line), styled as the user's own
// web projects style theirs: each line through the console's method of its level, so the developer tools' level
// filter works, the level as a small badge, Trace in grey and Debug in blue, Fatal as a red badge. The level is
// NLog's ordinal, 0 Trace to 5 Fatal; the label is the time and the thread.
const badge = 'border: 1px solid; border-radius: 3px; padding: 0px 2px;';
const wideBadge = 'border: 1px solid; border-radius: 3px; padding: 0px 5px 0px 6px;';
const dark = typeof globalThis.matchMedia === 'function' && globalThis.matchMedia('(prefers-color-scheme: dark)').matches;
globalThis.waveDudeLog = (level, label, message) => {
	switch (level) {
		case 0:
			console.debug('%c%s%cTRACE%c %s', 'margin-left: 10px; color: grey', label, 'color: grey; ' + badge, 'color: grey; border: none', message);
			break;
		case 1:
			console.debug('%c%s%cDEBUG%c %s', 'margin-left: 10px; color: DodgerBlue', label, 'color: DodgerBlue; ' + badge, 'color: DodgerBlue; border: none', message);
			break;
		case 2:
			console.info('%c%s%cINFO%c %s', 'margin-left: 10px', label, wideBadge, 'border: none', message);
			break;
		case 3:
			console.warn('%s%cWARN%c %s', label, wideBadge, 'border: none', message);
			break;
		case 4:
			console.error('%s%cERROR%c %s', label, badge, 'border: none', message);
			break;
		default:
			console.error('%s%cFATAL%c %s', label,
				'font-weight: bold; border: 1px solid red; border-radius: 3px; padding: 0px 2px; background-color: red; color: ' + (dark ? 'black' : 'white'),
				'font-weight: bold; border: none;', message);
	}
};

// The notice a crash leaves on the page (2026-11-14, the user's request): "WaveDude Next has stopped", the error's
// words and a button that loads the page again. A page is not a process: when the program in it dies, the page
// stands there as it was and no longer answers, and what happened is a line in a console a visitor does not open.
// Plain elements over everything, not the application's own interface, which is what has stopped. The program calls
// this for an error nothing handled on one of its threads (Program.cs), this script for the runtime's own end and
// for an error report the program can no longer take (below). The first call's notice stays.
let stopped = false;
globalThis.waveDudeStopped = message => {
	if (stopped) { return; }
	stopped = true;
	const make = (tag, style, text) => {
		const element = document.createElement(tag);
		element.style.cssText = style;
		element.textContent = text;
		return element;
	};
	const veil = make('div', 'position: fixed; inset: 0; z-index: 2147483647; display: flex; align-items: center; justify-content: center;'
		+ ' background: rgba(0, 0, 0, 0.55); font: 13px "Segoe UI", system-ui, sans-serif;', '');
	const box = make('div', 'max-width: min(640px, 90vw); padding: 20px 24px; background: #2d2d30; color: #f1f1f1; border: 1px solid #007acc;'
		+ ' box-shadow: 0 8px 32px rgba(0, 0, 0, 0.6);', '');
	box.setAttribute('role', 'alertdialog');
	box.setAttribute('aria-label', 'WaveDude Next has stopped');
	const reload = make('button', 'padding: 5px 16px; background: #007acc; color: #ffffff; border: 1px solid #007acc; font: inherit; cursor: pointer;',
		'Load the page again');
	reload.addEventListener('click', () => { globalThis.location.reload(); });
	box.append(
		make('div', 'font-size: 16px; font-weight: 600; margin-bottom: 8px;', 'WaveDude Next has stopped'),
		make('div', 'margin-bottom: 12px;', 'An error ended the program. Loading the page again starts it anew; the project that was open is lost.'
			+ ' The browser\'s console (F12) has the details.'),
		make('pre', 'margin: 0 0 16px; padding: 8px; max-height: 40vh; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere;'
			+ ' background: #1e1e1e; color: #d4d4d4; font: 12px Consolas, monospace;', message),
		reload);
	veil.append(box);
	document.body.append(veil);
	reload.focus();
};
// an error's words: its stack where it has one (which begins with its name and message), else whatever it says
const wordsOf = error => String(error && (error.stack || error.message) || error);

try {
	if (globalThis.crossOriginIsolated) {
		// by the server, or by the worker at the load isolate() asked for, whose note goes
		if (isolating()) { history.replaceState(null, ''); }
	} else {
		await isolate();
	}
	// The system's own font (system-font.js): where the visitor allowed it on an earlier visit its faces are read
	// from here on, beside the runtime's start, so the program finds them there and starts in them. Not waited
	// for, and no obstacle to the start should it fail to load.
	import('./system-font.js').catch(() => { });
	const { dotnet } = await import('./_framework/dotnet.js');

	// Blocking waits on threads that can call the page's scripts are allowed without a word. The runtime otherwise
	// warns at each one, with a stack it gathers for the purpose; the ones seen (2026-11-12, 65 in a few minutes) were
	// Avalonia's render thread waiting a moment for SkiaSharp's own lock around its table of native objects, taken
	// when a text blob is disposed while another thread makes or frees a Skia object: a short wait inside libraries
	// this application cannot change, and one that needs nothing from the waiting thread's scripts.
	//
	// The runtime's compiler for the code it interprets is off (the two parts of it that run on the threaded
	// runtime, by the runtime's own options; the third is off there anyway and says so when it is named). It
	// builds small WebAssembly modules that take the program's memory in with a ceiling of 2 GB written into them,
	// and this program's memory is built with its ceiling at 4 GB, so the browser refuses every one of them
	// ("memory import has a larger maximum size 65536 than the module's declared maximum 32768") and the runtime
	// would give up only after logging each failure with a dump of the module (2026-11-13). This build is compiled
	// ahead of time; what is interpreted in it is the little that compilation leaves over.
	const runtime = await dotnet.withConfig({
		jsThreadBlockingMode: 'DangerousAllowBlockingWait',
		runtimeOptions: ['--no-jiterpreter-traces-enabled', '--no-jiterpreter-jit-call-enabled'],
	}).create();
	// the GPU engine's script (webgpu-engine.js) writes what it reads from the card straight into the program's memory
	globalThis.waveDudeRuntime = runtime;
	// The runtime's own end, an abort or an exit, which no handler of the program's hears (it is the program that is
	// gone): the notice, and then whatever the runtime had hooked there itself.
	const module = runtime.Module;
	const onAbort = module.onAbort;
	module.onAbort = reason => {
		globalThis.waveDudeStopped('The runtime was aborted: ' + wordsOf(reason));
		if (onAbort) { onAbort(reason); }
	};
	const onExit = module.onExit;
	module.onExit = code => {
		globalThis.waveDudeStopped('The runtime ended, with exit code ' + code + '.');
		if (onExit) { onExit(code); }
	};
	await runtime.runMain(runtime.getConfig().mainAssemblyName, [globalThis.location.href]);

	// Every input event wakes the application's UI thread (Program.Wake): on the threaded runtime that thread sleeps
	// until one of its own timers is due or a job is posted to it, and Avalonia queues the browser's input for it
	// without waking it, so input showed late or, with nothing else to wake the thread, never. The call is made after
	// the event's own handlers have run, in which Avalonia queues the event, so the wake-up finds it there. Only now,
	// with the application set up: its UI thread exists.
	const exports = await runtime.getAssemblyExports('WaveDudeNext.Browser');
	const wake = () => { exports.WaveDudeNext.Browser.Program.Wake(); };
	const afterTheHandlers = () => { setTimeout(wake, 0); };
	for (const type of ['pointermove', 'pointerdown', 'pointerup', 'pointercancel', 'pointerover', 'pointerout', 'wheel',
		'keydown', 'keyup', 'beforeinput', 'input', 'compositionstart', 'compositionupdate', 'compositionend',
		'paste', 'copy', 'cut', 'dragenter', 'dragover', 'dragleave', 'drop', 'contextmenu', 'focusin', 'focusout',
		'touchstart', 'touchmove', 'touchend', 'touchcancel']) {
		globalThis.addEventListener(type, afterTheHandlers, { capture: true, passive: true });
	}
	wake();

	// An error of the page's own scripts that nothing caught, and a promise whose rejection nothing took, go into
	// the application's log (Program.PageError), so they show in its status bar and Messages pane and not in this
	// console alone. Such an error need not end the program, so it is not the notice; but where the program can no
	// longer take the report, it is gone, and the notice says so. Errors of scripts from elsewhere (an extension's)
	// are none of ours.
	const report = message => {
		try {
			exports.WaveDudeNext.Browser.Program.PageError(message).catch(() => { globalThis.waveDudeStopped(message); });
		} catch {
			globalThis.waveDudeStopped(message);
		}
	};
	globalThis.addEventListener('error', event => {
		if (!event.filename || event.filename.startsWith(globalThis.location.origin)) {
			report(wordsOf(event.error || event.message));
		}
	});
	globalThis.addEventListener('unhandledrejection', event => { report('A promise was rejected and nothing took it: ' + wordsOf(event.reason)); });
} catch (error) {
	document.getElementById('out').textContent = 'WaveDude Next could not start: ' + (error && error.message || error);
	document.getElementById('out').style.cssText = 'color: #f1f1f1; font: 14px sans-serif; padding: 16px;';
	throw error;
}
