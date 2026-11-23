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

// The interpreter's own compiler in a program whose memory may grow to 4 GB (2026-11-23). Linked into the runtime's
// script (--pre-js in the project file), so it runs once in every thread of the page: each thread compiles for itself.
//
// Where the program is interpreted (a Debug build as a whole, a few methods of a compiled one), the runtime counts how
// often a stretch of code is entered and turns the hot ones into small WebAssembly modules of their own, which then
// run in the interpreter's place (the "jiterpreter"). Each such module takes the program's memory in, and with
// threads the runtime's script writes into it that this memory grows to 32768 pages, 2 GB, at the most
// (dotnet.runtime.js of 10.0.15: appendULeb(256), appendULeb(32768)). This head's memory may grow to 65536 pages, and
// a module is refused a memory that may grow further than the module says: "memory import has a larger maximum size
// 65536 than the module's declared maximum 32768", for every one of them. From 2026-11-13 the compiler was therefore
// switched off (two runtime options in main.js), and a Debug build ran on the bare interpreter.
//
// The two numbers are written the same way, three bytes that differ in the last (80 80 02 and 80 80 04), and every
// such module is made by "new WebAssembly.Module(bytes)". So that constructor is stood in for here: a module that
// names the memory "m"."h" as shared with at least 256 and at most 32768 pages gets a copy of its bytes with the
// most at 65536; every other module passes through as it is. Nothing of the runtime's own files is changed.
(function () {
	if (typeof WebAssembly === 'undefined' || WebAssembly.Module.widensMemory) { return; }
	const Module = WebAssembly.Module;
	// the name "m", the name "h", a memory (2), shared with a ceiling (3), at least 256 pages, at most 32768
	const declared = [0x01, 0x6d, 0x01, 0x68, 0x02, 0x03, 0x80, 0x02, 0x80, 0x80, 0x02];
	// where the declaration's last byte stands in the module, or -1
	const find = bytes => {
		const last = bytes.length - declared.length;
		for (let at = 8; at <= last; at++) {
			let same = 0;
			while (same < declared.length && bytes[at + same] === declared[same]) { same++; }
			if (same === declared.length) { return at + declared.length - 1; }
		}
		return -1;
	};
	const widened = source => {
		if (!(source instanceof ArrayBuffer) && !ArrayBuffer.isView(source)) { return source; }
		const bytes = source instanceof ArrayBuffer ? new Uint8Array(source) : new Uint8Array(source.buffer, source.byteOffset, source.byteLength);
		const at = find(bytes);
		if (at < 0) { return source; }
		// a copy of its own: the bytes handed over are the runtime's
		const copy = bytes.slice();
		copy[at] = 0x04;
		return copy;
	};
	const Widening = function (bytes, ...more) { return new Module(widened(bytes), ...more); };
	Widening.prototype = Module.prototype;
	for (const name of ['imports', 'exports', 'customSections']) { Widening[name] = Module[name]; }
	Widening.widensMemory = true;
	WebAssembly.Module = Widening;
})();
