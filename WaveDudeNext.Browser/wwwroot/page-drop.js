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

// A drop from outside the page, read as the page itself gets it (PageDrops.cs; 2026-11-20). Avalonia.Browser's own
// script hands .NET the browser's live items of a drop, which the browser empties when the drop's handlers have
// returned, and on the threaded runtime .NET reads them only after that: every drop arrived in the program with no
// data at all, a project file's as much as dragged text. Here the drop's text and its files are taken while the event
// is being handled (the window's capturing phase, ahead of Avalonia's handler) and kept until the program, which
// meets its empty drop a moment later, asks. A file object stays readable after the event; its bytes are read only
// when the program wants them. A new drag forgets the last drop.

let held = null;   // the last drop: its text, its files, and the bytes of the one read last

globalThis.addEventListener('dragenter', () => { held = null; }, { capture: true });
globalThis.addEventListener('drop', event => {
	const data = event.dataTransfer;
	held = data ? { text: data.getData('text/plain'), files: [...data.files], bytes: null } : null;
}, { capture: true });

// the dropped text, '' for none
export function text() { return held ? held.text : ''; }

// how many files were dropped, and the name and the length in bytes of one
export function count() { return held ? held.files.length : 0; }
export function name(index) { return held.files[index].name; }
export function length(index) { return held.files[index].size; }

// reads a file's bytes and resolves to their number; copy then puts them into the program's memory at an address
// (which arrives as a signed number), and the page's copy goes
export async function read(index) {
	held.bytes = new Uint8Array(await held.files[index].arrayBuffer());
	return held.bytes.length;
}
export function copy(address) {
	globalThis.waveDudeRuntime.localHeapViewU8().set(held.bytes, address >>> 0);
	held.bytes = null;
}
