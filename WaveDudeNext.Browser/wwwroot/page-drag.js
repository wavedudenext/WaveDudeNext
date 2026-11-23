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

// A drag that the browser itself carries, so that it can leave the page (PageDrags.cs; 2026-11-22): a row of the
// project tree dragged into another tab of the application, or into a text editor. Avalonia has no drag source in a
// browser: a drag it starts lives inside the page. A browser starts a drag of its own only from the user's press on
// an element marked as draggable, and only where the press was left to it, while Avalonia's script cancels every
// press's default. So the program says what the pointer is over (arm); a press on that is left to the browser, which
// starts the drag at its own threshold; here the drag gets its text, and the program is told that it started and
// when it ended. The mouse's left button alone: a finger's or a pen's drag stays the program's own.

let armed = null;      // the text a press may drag away, while the pointer is over such a thing
let carried = false;   // a drag of ours is in the browser's hands
let program;           // the program's side: Pressed, DragStarted, DragEnded
let host;              // the application's element
let ghost;             // what the browser shows under the pointer in place of a picture of the whole page: nothing

function ended() {
	carried = false;
	host.draggable = armed !== null;
	program.DragEnded();
}

// the listeners are the page's from here on
export async function init() {
	program = (await globalThis.waveDudeRuntime.getAssemblyExports('WaveDudeNext.Browser')).WaveDudeNext.Browser.PageDrags;
	host = document.getElementById('out');
	ghost = document.createElement('canvas');
	ghost.width = ghost.height = 1;
	ghost.style.cssText = 'position:fixed;left:-8px;top:-8px;pointer-events:none';
	document.body.appendChild(ghost);
	// ahead of Avalonia's own handler, which would cancel the press's default and with it the browser's drag: for a
	// press on what is armed its cancelling does nothing
	globalThis.addEventListener('pointerdown', event => {
		// a press while a drag is still ours: the browser never said that it ended
		if (carried) { ended(); }
		if (armed === null || event.button !== 0 || event.pointerType !== 'mouse') { return; }
		Object.defineProperty(event, 'preventDefault', { value() { } });
		program.Pressed();
	}, { capture: true });
	host.addEventListener('dragstart', event => {
		if (armed === null) { event.preventDefault(); return; }
		event.dataTransfer.setData('text/plain', armed);
		event.dataTransfer.effectAllowed = 'copyMove';
		event.dataTransfer.setDragImage(ghost, 0, 0);
		carried = true;
		program.DragStarted();
	});
	host.addEventListener('dragend', () => { if (carried) { ended(); } });
}

// what the pointer is over: the text a press there drags away, '' for nothing
export function arm(text) {
	armed = text === '' ? null : text;
	if (!carried) { host.draggable = armed !== null; }
}
