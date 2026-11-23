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

// The frame cache in the browser's own storage for this page (the Origin Private File System), for OpfsFrameStorage:
// the part of a run's frames that does not fit the memory goes to a file there, as it goes to a file on a drive in
// the desktop heads. The files are held by a worker (frame-storage-worker.js: a page's own thread gets no
// synchronous access to them); this module passes the program's requests on and answers when the worker has.

let worker;      // the worker once it runs
let starting;    // the promise of it, from the first request on
let nextRequest = 1;
const pending = new Map();

// Resolves to the worker. It is made from its script's text, not from the script's address (2026-11-14): this page
// is cross-origin isolated, as the threaded runtime needs it, and such a page may start a worker from an address
// only when the server sends the embedder-policy header with that script too. The .NET SDK's own host, which Start
// runs, sends the header with the page and the runtime's files and not with the page's own scripts, so the worker
// was refused there without a word ("the frame cache's worker failed: no reason given"). A worker made from a blob
// takes its policy from the page that makes it, whatever the server sent. The blob's address is kept for the
// page's life: one script.
function start() {
	if (!starting) {
		starting = (async () => {
			const response = await fetch(new URL('./frame-storage-worker.js', import.meta.url));
			if (!response.ok) { throw new Error('the frame cache\'s worker script could not be loaded: ' + response.status); }
			const made = new Worker(URL.createObjectURL(new Blob([await response.text()], { type: 'text/javascript' })));
			made.onmessage = event => {
				const answer = event.data;
				const waiting = pending.get(answer.id);
				if (!waiting) { return; }
				pending.delete(answer.id);
				if (answer.error !== undefined) { waiting.reject(new Error(answer.error)); } else { waiting.resolve(answer.result); }
			};
			made.onerror = event => {
				// the worker itself failed: nobody would ever answer; the next request starts another
				const error = new Error('the frame cache\'s worker failed: ' + (event.message || 'no reason given'));
				for (const waiting of pending.values()) { waiting.reject(error); }
				pending.clear();
				worker = undefined;
				starting = undefined;
			};
			worker = made;
			return made;
		})();
		// a script that could not be fetched is asked for again at the next request
		starting.catch(() => { starting = undefined; });
	}
	return starting;
}

// The page is being left: closed, reloaded or gone to another address. The browser's storage has no file that
// deletes itself with the page, as a drive's has with its process, so the run's file would lie there until the
// application is next opened here; the worker is told to give its room back while it still can (2026-11-13, the
// user's point that the browser says when a page is unloaded). Not where the page is only put aside to be shown
// again as it was (persisted): its run is still its run then. Nothing can be waited for here.
globalThis.addEventListener('pagehide', event => {
	if (worker && !event.persisted) { worker.postMessage({ id: 0, op: 'release' }); }
});

// The request is put together when the worker is there to take it (a function, where it names the program's
// memory: that is asked for at the moment of sending).
function ask(message) {
	return start().then(running => new Promise((resolve, reject) => {
		const id = nextRequest++;
		pending.set(id, { resolve, reject });
		running.postMessage({ id, ...(typeof message === 'function' ? message() : message) });
	}));
}

// the program's memory, shared between its threads; asked for afresh at every use, since it is another buffer after
// the memory has grown (main.js keeps the runtime's object for this)
const memory = () => globalThis.waveDudeRuntime.localHeapViewU8().buffer;

// the bytes the browser still lets this page store: its quota less what is used
export async function free() {
	if (!navigator.storage || !navigator.storage.estimate || !navigator.storage.getDirectory) { throw new Error('this browser has no storage for a page'); }
	const estimate = await navigator.storage.estimate();
	return Math.max(0, (estimate.quota || 0) - (estimate.usage || 0));
}

// the bytes of the page's storage that anything but this session's own files holds: the application's other tabs
export function others() { return ask({ op: 'others' }); }

// a new, empty file; resolves to its number
export function open(name) { return ask({ op: 'open', name }); }

export function setLength(file, bytes) { return ask({ op: 'size', file, bytes }); }

// length bytes of the program's memory at address to the file at a byte offset, and back; read resolves to the bytes
// read (an address arrives as a signed number: above 2 GB it would be a negative one)
export function write(file, address, length, at) { return ask(() => ({ op: 'write', file, memory: memory(), address: address >>> 0, length, at })); }

export function read(file, address, length, at) { return ask(() => ({ op: 'read', file, memory: memory(), address: address >>> 0, length, at })); }

// closes the file, which removes it
export function close(file) { return ask({ op: 'close', file }); }

// removes what earlier sessions left behind, not the files of the application's other tabs; resolves to how many
// files went
export function sweep() { return ask({ op: 'sweep' }); }
