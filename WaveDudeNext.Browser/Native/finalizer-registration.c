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

// The runtime that stopped now and then in long runs (2026-11-21; known since 2026-11-14: an assertion in the
// collector or a memory access out of bounds, a few runs in a hundred, always on a dead object).
//
// An object of a class with a finaliser is registered for finalisation the moment it has been allocated
// (mono_object_new_alloc_specific_checked in the runtime's object.c, read at v10.0.15), by
// sgen_object_register_for_finalization (sgen-fin-weak-hash.c). That function notes the object in a table of 1024
// entries, without a lock; when the table is full it takes the collector's lock to empty it, and while another thread
// empties it, it sleeps 200 microseconds at a time. Both are waits during which the thread counts as stopped, so
// another thread's collection goes ahead meanwhile; the function's own comments say so ("a GC was triggered, which
// then drained the stage entries").
//
// At that point the new object is held by nothing but the C functions' own variables: it has not yet been handed to
// the code that asked for it. On every other platform that is enough, the collector reading a thread's stack and
// registers and leaving alone whatever they point at. In WebAssembly those variables are the machine's locals, which
// nothing can read, and the runtime keeps a reference visible only where compiled or interpreted code holds it. So a
// collection during that wait takes the object for dead: its memory is given out again, or filled, and the code
// that asked for the object is handed an address that is no object any more. With threads only: a single thread
// never waits for that lock. The runtime knows the hazard, and guards the place where a finaliser is run against it
// ("we explicitly pin the object via a gchandle so we don't rely on the ref being present on stack/regs which is not
// scannable on WASM", sgen_gc_invoke_finalizers), but not this one.
//
// What it did here: every Skia and HarfBuzz object has a finaliser, and so has every weak reference SkiaSharp makes
// for one, so Avalonia's text drawing registers hundreds a second while the program's other threads allocate. The
// stops were at exactly those objects: a font or a text blob builder just made, whose first virtual call met the
// collector's filler (class-accessors.c:90), or a collection copying such a remnant (sgen-marksweep.c:328,
// sgen-scan-object.h:93).
//
// The project file links the function below in the runtime's place (-Wl,--wrap, as thread-id.c is): the object is
// pinned by a handle for the length of the registration, which is the runtime's own remedy in the place it did
// guard. To go when the runtime this head builds with registers a new object safely itself.
#include <stdint.h>

// the collector's own functions (sgen-gchandles.c), which the runtime's library exports to its other files
uint32_t sgen_gchandle_new(void *obj, int pinned);
void sgen_gchandle_free(uint32_t gchandle);

void __real_sgen_object_register_for_finalization(void *obj, void *user_data);

void __wrap_sgen_object_register_for_finalization(void *obj, void *user_data)
{
	uint32_t pin = sgen_gchandle_new(obj, 1);
	__real_sgen_object_register_for_finalization(obj, user_data);
	sgen_gchandle_free(pin);
}
