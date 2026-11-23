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

// The page's loading mark (index.html) goes when the program's first picture is on the screen: the program calls this
// then (Program.cs), once. The application's element takes the keyboard with it, unless the visitor has put it
// elsewhere meanwhile: a key pressed before the first click is the application's, as in a window that has just opened.
//
// The mark's bar is the whole way to that picture, not the download alone (2026-11-22, the user's report of a long
// gap after "Starting…": the bar had been full when the last file was in and then stood for the three or four
// seconds the program takes to start, eight in a Debug build). The download is its first three fifths. The rest is
// the start's four stages: the files are in and the runtime starts; the program's Main runs; the application is set
// up; its view is made and drawn. The first is known here, the others are the program's to tell (waveDudeStage,
// from Program.cs). Each stage has the share of the bar that its time is of the start's in the compiled build on the
// machine it was measured on (0.6, 0.2, 1.2 and 0.9 s). During a stage the bar goes on towards the stage's end over
// five times that, fast at first and ever slower, so it is still moving in a Debug build, where a stage takes four
// times as long (with twice the time it stood still for two seconds); the next stage takes it on from wherever it is.
const downloadShare = 0.6;
const stages = [{ to: 0.68, seconds: 3 }, { to: 0.71, seconds: 1 }, { to: 0.87, seconds: 6 }, { to: 1, seconds: 5 }];
let stageShown = -1;
const moveBar = (share, seconds) => {
	const mark = document.getElementById('loading');
	if (mark) {
		mark.style.setProperty('--t', seconds + 's');
		mark.style.setProperty('--p', share);
	}
};
globalThis.waveDudeStage = stage => {
	if (stage <= stageShown || !stages[stage]) { return; }
	stageShown = stage;
	// what is going on, beside the megabytes, which are the download's own display and stay; in the language the
	// page settled for the mark (index.html: the program's setting, else the browser's)
	const status = document.getElementById('loading-status');
	if (status) { status.textContent = globalThis.waveDudeLoading.starting; }
	moveBar(stages[stage].to, stages[stage].seconds);
};
globalThis.waveDudeStarted = () => {
	const mark = document.getElementById('loading');
	if (mark) { mark.remove(); }
	if (document.activeElement === document.body || !document.activeElement) {
		document.getElementById('out').focus({ preventScroll: true });
	}
};

// While the application has the keyboard, the browser's own answer to a key is held back at the moment the key
// arrives (2026-11-20, the user's word on two faults known since 2026-11-15: every Tab moved the browser's focus off
// the application's element, after which no key reached the application any more, and F1 opened the manual and the
// browser's own help; F5, the application's Run, loaded the page again, F6, F7 and F10 went to the browser's
// toolbar, Ctrl+S saved the page). Avalonia's script does hold it back for every key, but only when .NET has
// answered (input.ts at 12.1.5: OnKeyDown(…).then(… preventDefault())), which on the threaded runtime is after
// the browser has acted. The key itself still goes to Avalonia as before: nothing is stopped here but the default.
//
// Left to the browser, since the application has no use for them and the visitor does:
//   - a character being typed (a key that is a character, without Ctrl, Alt or the Mac's command key; AltGr's
//     characters; a dead key or an input method at work), exactly as it was before this handler;
//   - F11 and F12, the full screen and the developer tools;
//   - the browser's zoom, Ctrl with plus, minus or 0 (View > UI zoom names it as the zoom of this version);
//   - loading the page again by Ctrl+R or Ctrl+F5. F5 alone is Run and Shift+F5 is Stop, as on a desktop;
//   - Ctrl+V in a browser that gives a page no way to read the clipboard but its own paste event, which follows
//     the key only when the key's default stands (Avalonia leaves it standing there for the same reason).
// The keys a browser keeps for itself whatever a page does (new tab, new window, close) never get here.
const application = document.getElementById('out');
const browsersOwn = event => {
	const key = event.key;
	if (event.isComposing || key === 'Dead' || key === 'Process' || key === 'Unidentified') { return true; }
	const command = event.ctrlKey || event.metaKey;
	if (key.length === 1 && (event.getModifierState('AltGraph') || (!command && !event.altKey))) { return true; }
	if (key === 'F11' || key === 'F12') { return true; }
	if (!command || event.altKey) { return false; }
	if ((key === 'v' || key === 'V') && !(navigator.clipboard && (navigator.clipboard.read || navigator.clipboard.readText))) { return true; }
	return key === '+' || key === '-' || key === '=' || key === '_' || key === '0' || key === 'r' || key === 'R' || key === 'F5';
};
globalThis.addEventListener('keydown', event => {
	if (application.contains(event.target) && !browsersOwn(event)) { event.preventDefault(); }
}, { capture: true });

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
	// a program that stopped before it drew leaves the page's loading mark turning: nothing is loading any more
	globalThis.waveDudeStarted();
	document.body.append(veil);
	reload.focus();
};
// an error's words: its stack where it has one (which begins with its name and message), else whatever it says
const wordsOf = error => String(error && (error.stack || error.message) || error);
// A start that failed: its words in the loading mark's place (the text replaces it), in the page's own colours,
// which follow the desktop's scheme. The first failure's words stay.
let startHasFailed = false;
const startFailed = error => {
	if (startHasFailed) {
		return;
	}
	startHasFailed = true;
	const out = document.getElementById('out');
	out.textContent = 'WaveDude Next could not start: ' + (error && error.message || error);
	out.style.cssText = 'font: 14px sans-serif; padding: 16px;';
};

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
	//
	// Every assembly is in the program's memory before the runtime starts (2026-11-21, on the user's word to follow
	// a start that ended with "Could not load file or assembly 'System.Xml.ReaderWriter'", once in 56 loads, the kind
	// he had met himself on 2026-11-14 with 'System.Net.Primitives'). The runtime's loader tells the "core"
	// assemblies, which it puts into memory first, from the rest, which on the threaded runtime it goes on
	// registering from this thread while the runtime is already starting on its own (startup.ts and loader/assets.ts
	// at v10.0.15). The table they are registered in has no lock (bundled-resources.c), and the starting runtime
	// writes it too, with its time-zone data (mono_wasm_load_runtime in driver.c): two writers at once, one of
	// whose entries can be lost, and the lost assembly is then not found by whoever asks for it first, which the
	// runtime remembers for good. A published build lists every assembly as core and has no such moment; a build
	// that is not published, which is what Start serves in Debug, lists seven, with some 190 in the rest. So the
	// rest is made core here, the symbol files with it, before the loader looks at the lists.
	// The loading mark's bar and megabytes (index.html; 2026-11-22, the user's choice of look): how far the download
	// is, in the megabytes that come over the network. The runtime says only how many files it has, and one of them,
	// the compiled program, is three fifths of the download: a bar by files would stand for most of the time and
	// then jump. So the files are fetched here, in the runtime's place (its own hook for that), and read as they
	// arrive. A server announces a file's length as it sends it, compressed, while a page sees the bytes unpacked:
	// a file under way counts by what has arrived of it over what it unpacks to, which for the compiled program is
	// 3.4 times its length under gzip and 5 times under brotli (measured; any other file is there in a moment).
	// The whole is known almost from the start: the runtime asks for its files sixteen at a time, by which the
	// lengths would trickle in and the whole grow under the bar; so every file on the runtime's list is asked for
	// here the moment the list is there, all at once, and handed over when the runtime comes for it. Until each of
	// them has announced itself the caption names what has arrived and no whole. (A request for the lengths alone,
	// ahead of the files, was tried: the development server answers such a request without a length.) A file is
	// checked against its hash here, as the browser checks it for the runtime, so it is whole before it is handed
	// on. Should any of this fail for a file, the runtime's own way fetches it and the bar counts it as arrived.
	const megabyte = 1048576;
	const unpacked = { gzip: 3.4, br: 5 };
	const files = new Map();     // by name: the length announced, what a byte of it unpacks to, the bytes received, done
	const begun = new Map();     // by name: the file as it is being fetched ahead of the runtime's asking, a promise
	let listed = 0;              // how many files were begun so
	let shownShare = 0;
	let starting = false;
	// the files are in: the first stage of the start (the stages are at the loading mark's functions, above)
	const showStarting = () => {
		starting = true;
		globalThis.waveDudeStage(0);
	};
	const showDownload = () => {
		const mark = document.getElementById('loading');
		const size = document.getElementById('loading-size');
		if (!mark || !size) { return; }
		let arrived = 0;
		let announced = 0;
		let waiting = files.size < listed;
		for (const file of files.values()) {
			announced += file.length;
			arrived += file.done ? file.length : Math.min(file.length, file.received / file.ratio);
			waiting ||= !file.done;
		}
		if (announced === 0) { return; }
		// The megabytes are a display of their own (the user's word: they had gone when the program's start took the
		// caption): they stay when the download is over, and a file that comes while the program starts still counts.
		// In the mark's language (index.html): its decimal sign and its word for "of".
		const megabytes = bytes => (bytes / megabyte).toLocaleString(globalThis.waveDudeLoading.language, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
		size.textContent = megabytes(arrived) + (files.size < listed ? ' MB' : ' ' + globalThis.waveDudeLoading.of + ' ' + megabytes(announced) + ' MB');
		if (starting) { return; }
		if (!waiting) {
			// every file is here: from now until its first picture the program is starting
			showStarting();
			return;
		}
		// files that have not announced themselves yet are reckoned at the mean of those that have, for the bar alone
		const expected = announced * Math.max(1, listed / files.size);
		shownShare = Math.max(shownShare, Math.min(1, arrived / expected));
		moveBar(downloadShare * shownShare, 0.15);
	};
	const followed = async (name, address, integrity) => {
		// a file with a hash is the one of that hash under any name it was ever stored by
		const response = await fetch(address, { credentials: 'same-origin', cache: integrity ? 'force-cache' : 'no-cache' });
		if (!response.ok || !response.body) { return response; }
		const file = {
			length: Number(response.headers.get('Content-Length')) || 0,
			ratio: unpacked[response.headers.get('Content-Encoding')] || 1,
			received: 0,
			done: false,
		};
		files.set(name, file);
		const parts = [];
		const reader = response.body.getReader();
		for (;;) {
			const { done, value } = await reader.read();
			if (done) { break; }
			parts.push(value);
			file.received += value.length;
			showDownload();
		}
		const bytes = new Uint8Array(file.received);
		let at = 0;
		for (const part of parts) {
			bytes.set(part, at);
			at += part.length;
		}
		parts.length = 0;
		if (integrity && integrity.startsWith('sha256-')) {
			let hash = '';
			for (const byte of new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))) { hash += String.fromCharCode(byte); }
			if ('sha256-' + btoa(hash) !== integrity) { throw new Error('the file at ' + address + ' is not the one this build names (its hash is another)'); }
		}
		// a server that announced no length: what arrived, as it would have come packed
		if (file.length === 0) { file.length = file.received / file.ratio; }
		file.done = true;
		showDownload();
		// the bytes are unpacked: the headers that say otherwise go
		const headers = new Headers(response.headers);
		headers.delete('Content-Encoding');
		headers.delete('Content-Length');
		return new Response(bytes, { status: response.status, statusText: response.statusText, headers });
	};

	const runtime = await dotnet.withConfig({
		jsThreadBlockingMode: 'DangerousAllowBlockingWait',
		runtimeOptions: ['--no-jiterpreter-traces-enabled', '--no-jiterpreter-jit-call-enabled'],
	}).withResourceLoader((type, name, address, integrity) => {
		// the runtime's own scripts are the browser's to load: a script is given by its address, not by its bytes
		if (type === 'dotnetjs' || !document.getElementById('loading')) { return undefined; }
		// the file begun ahead, once: a second asking for the same file is the runtime trying again, and fetches anew
		const ahead = begun.get(name);
		begun.delete(name);
		return (ahead || followed(name, address, integrity)).catch(error => {
			console.warn('The loading mark could not follow ' + name + ' (' + (error && error.message || error) + '); the runtime loads it its own way.');
			const file = files.get(name);
			files.set(name, { length: file ? file.length : 0, ratio: 1, received: 0, done: true });
			showDownload();
			return fetch(address, integrity ? { integrity } : undefined);
		});
	}).withOnConfigLoaded(config => {
		const resources = config.resources;
		if (resources && resources.coreAssembly && resources.assembly && resources.assembly.length > 0) {
			resources.coreAssembly.push(...resources.assembly);
			resources.assembly = [];
		}
		if (resources && resources.pdb && resources.pdb.length > 0) {
			resources.corePdb = (resources.corePdb || []).concat(resources.pdb);
			resources.pdb = [];
		}
		// the loading mark: every file of the list is asked for now (the files lie beside the runtime's script); a
		// failure waits for the runtime's asking, which then goes its own way
		if (resources && document.getElementById('loading')) {
			const beside = new URL('./_framework/', import.meta.url);
			for (const list of [resources.wasmNative, resources.coreAssembly, resources.corePdb]) {
				for (const entry of list || []) {
					if (entry && entry.name && !begun.has(entry.name)) {
						const file = followed(entry.name, new URL(entry.name, beside).href, entry.hash);
						file.catch(() => { });
						begun.set(entry.name, file);
						listed++;
					}
				}
			}
		}
	}).create();
	// the runtime is up: whatever the bar made of the download, the program is starting now
	showStarting();
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
	startFailed(error);
	throw error;
}
