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

// The worker that holds the frame cache's files in the browser's own storage for this page (the Origin Private File
// System), for frame-storage.js. A browser gives a page synchronous access to such a file in a dedicated worker
// alone, and one handle a file, so every read and write of the program's threads comes through here, in the order it
// was asked. A read or write is made straight from or into the program's memory, which is shared between the threads
// and comes along with each request (the memory's buffer is another object after it has grown).
//
// A request: { id, op, ... }; the answer: { id, result } or { id, error }.

const folderName = 'frame-cache';
const files = new Map();
let nextFile = 1;
let folder;

// Two tabs of the application share the storage, and a tab that starts sweeps what earlier sessions left. A file
// held open is safe from that by the browser's own rule (it cannot be removed), but a file is made before its handle
// can be taken, and one found in between was removed by another tab's sweep (2026-11-14, the user's question, seen
// with two tabs). So a session's files are marked as a living session's by a lock of the browser's (Web Locks) named
// after the session, taken before the session's first file is made and never given back: the browser releases it
// when the page goes, however it goes, and the sweep leaves the files of every session whose lock is held. The
// session is the mark in a file's name (OpfsFrameStorage.NewPath: run-<date>-<time>-<session>-<n>). Where a browser
// has no such locks, the open handle alone protects, as before.
const lockPrefix = 'wavedude-frame-cache-';
const sessions = new Map();

function sessionOf(name) {
	const match = /^run-\d{8}-\d{6}-([0-9a-f]+)-\d+\./.exec(name);
	return match ? match[1] : undefined;
}

// resolves once the session's lock is held (at once where it is, or where there are no locks)
function hold(session) {
	if (session === undefined || !navigator.locks) { return Promise.resolve(); }
	let held = sessions.get(session);
	if (!held) {
		held = new Promise(taken => {
			// shared, so that nothing ever waits for it; the callback's promise never settles: the lock is this worker's
			// until the worker ends. A browser that refuses leaves the session without the mark.
			navigator.locks.request(lockPrefix + session, { mode: 'shared' }, () => {
				taken();
				return new Promise(() => { });
			}).catch(() => taken());
		});
		sessions.set(session, held);
	}
	return held;
}

async function cache() {
	if (!folder) {
		const root = await navigator.storage.getDirectory();
		folder = await root.getDirectoryHandle(folderName, { create: true });
	}
	return folder;
}

function fileOf(number) {
	const file = files.get(number);
	if (!file) { throw new Error('the file is closed'); }
	return file;
}

async function perform(message) {
	switch (message.op) {
		// a new, empty file by its name; answers the file's number
		case 'open': {
			await hold(sessionOf(message.name));
			const directory = await cache();
			const handle = await directory.getFileHandle(message.name, { create: true });
			const access = await handle.createSyncAccessHandle();
			const number = nextFile++;
			files.set(number, { name: message.name, access });
			return number;
		}
		case 'size':
			fileOf(message.file).access.truncate(message.bytes);
			return 0;
		// length bytes of the program's memory at address, to the file at a byte offset
		case 'write': {
			const access = fileOf(message.file).access;
			const bytes = new Uint8Array(message.memory, message.address, message.length);
			let done = 0;
			while (done < message.length) {
				const written = access.write(done === 0 ? bytes : bytes.subarray(done), { at: message.at + done });
				if (!(written > 0)) { throw new Error('the storage took none of the bytes'); }
				done += written;
			}
			return done;
		}
		// the other way; answers the bytes read, fewer than asked at the file's end
		case 'read':
			return fileOf(message.file).access.read(new Uint8Array(message.memory, message.address, message.length), { at: message.at });
		// closed and removed: a store's file is a temporary one
		case 'close': {
			const file = files.get(message.file);
			if (!file) { return 0; }
			files.delete(message.file);
			file.access.close();
			await (await cache()).removeEntry(file.name);
			return 0;
		}
		// What of the page's storage is in use by anything but this session's own files, in bytes: the files of the
		// application's other tabs, which share the one storage (2026-11-21, the user's choice: the room a run's file
		// may take is one room for all tabs). The browser's figure for the whole counts a file by its length, as the
		// handles here report theirs.
		case 'others': {
			const estimate = await navigator.storage.estimate();
			let own = 0;
			for (const file of files.values()) { own += file.access.getSize(); }
			return Math.max(0, (estimate.usage || 0) - own);
		}
		// What earlier sessions left behind (a browser that was ended, a page that could not clean up as it went). The
		// files of a session that lives, this one's and those of the application's other tabs, stay. Answers how many
		// went.
		case 'sweep': {
			const directory = await cache();
			const own = new Set([...files.values()].map(file => file.name));
			const names = [];
			for await (const [name, handle] of directory.entries()) {
				if (handle.kind === 'file' && !own.has(name)) { names.push(name); }
			}
			// asked after the listing: a file in the list was made under its session's lock, so the lock of a session
			// that lives is among those held now
			const living = new Set();
			if (navigator.locks) {
				try {
					for (const lock of (await navigator.locks.query()).held) { living.add(lock.name); }
				} catch (error) {
					// no answer: the open handles protect
				}
			}
			let removed = 0;
			for (const name of names) {
				const session = sessionOf(name);
				if (session !== undefined && living.has(lockPrefix + session)) { continue; }
				try {
					await directory.removeEntry(name);
					removed++;
				} catch (error) {
					// held open by another tab
				}
			}
			return removed;
		}
		default:
			throw new Error('unknown request ' + message.op);
	}
}

// The page is going for good (frame-storage.js says so when the browser tells it): a last effort before this worker
// is ended with it. What is certain to be done is what can be done at once, without waiting for the browser: every
// file is cut to no length, which gives its room on the disk back, and closed. The names are then removed as well,
// if the worker lasts that long; an empty name that stays is taken by the next start's sweep. Nothing else here is
// of use afterwards: a request that still comes finds its file closed.
function release() {
	const names = [];
	for (const file of files.values()) {
		try {
			file.access.truncate(0);
			file.access.close();
		} catch (error) {
			// closed already, or the browser will not: the next start's sweep
		}
		names.push(file.name);
	}
	files.clear();
	cache().then(async directory => {
		for (const name of names) {
			try {
				await directory.removeEntry(name);
			} catch (error) {
				// the next start's sweep
			}
		}
	}, () => { });
}

// One request at a time, in the order they came: opening and closing wait for the browser, and a request that
// arrived meanwhile must not overtake them. The release alone is done the moment it comes, whatever else waits.
let chain = Promise.resolve();
onmessage = event => {
	const message = event.data;
	if (message.op === 'release') {
		release();
		return;
	}
	chain = chain.then(async () => {
		try {
			postMessage({ id: message.id, result: await perform(message) });
		} catch (error) {
			postMessage({ id: message.id, error: (error && error.name ? error.name + ': ' : '') + (error && error.message || error) });
		}
	});
};
