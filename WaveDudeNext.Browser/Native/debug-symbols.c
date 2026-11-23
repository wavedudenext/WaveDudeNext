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

// A Debug build that is not four times slower than it need be (2026-11-23).
//
// A Debug build starts the runtime with a debug level other than 0 (its boot configuration has -1), and the runtime
// hangs three things on that one number. Its loader fetches the symbol files (dotnet.js: "0 != debugLevel"). Its
// start-up code (mono_wasm_load_runtime_common in runtime.c, which lies in the pack as source, read at 10.0.15)
// then does, for any level but 0,
//
//     interp_opts = "-all,simd";                 // "Disable optimizations which interfere with debugging"
//     mono_wasm_enable_debugging (debug_level);
//
// so the interpreter runs with every optimisation off, its own compiler among them; and the second line is what
// later makes the debugger's part of the runtime call mono_debug_init, the one call that has the runtime read the
// symbol files, without which an error's stack trace names its methods but no file and line
// (mono_wasm_debugger_init in mini-wasm-debugger.c: "if (!debugger_enabled) return; mono_debug_init (...)", and with
// it sequence points in every method for a debugger to stop at). Measured here: the manual opened in 50 s at the
// level a Debug build has and in 12 s at level 0, where the stack traces had lost their lines.
//
// Nobody attaches a debugger to this head's managed code in the browser (none is set up). So the two calls are stood
// in for here (-Wl,--wrap in the project file, as thread-id.c is): the symbol files are read as before, the level is
// not handed to the debugger's part, and the interpreter is set up with the options the runtime had for it before
// the debug level changed them. With it the manual opens in 13.5 s, a run of 1250 frames takes 3.8 s where it took
// 5.5 s, and a stack trace still says "in ...\Program.cs:line 277". A compiled build does not come this way at all
// (the lines above are inside "#else" of ENABLE_AOT), so nothing changes there. What is given up: a debugger cannot
// attach to the managed code of a Debug build; for that the two --wrap flags are taken out of the project file.
#include <stddef.h>

// MonoDebugFormat's MONO_DEBUG_FORMAT_MONO (mono-debug.h)
#define DEBUG_FORMAT_MONO 1
void mono_debug_init(int format);

void __real_mono_ee_interp_init(const char *opts);

// whether the runtime asked for debugging, and so changed the interpreter's options
static int debugging_asked;

// in the place of mono_wasm_enable_debugging (mini-wasm.c), which only notes the level for the debugger's part
void __wrap_mono_wasm_enable_debugging(int debug_level)
{
	(void)debug_level;
	debugging_asked = 1;
	// before the first assembly is loaded: it reads an assembly's symbol file as the assembly comes
	mono_debug_init(DEBUG_FORMAT_MONO);
}

void __wrap_mono_ee_interp_init(const char *opts)
{
	// the runtime's own default (driver.c: const char *interp_opts = "")
	__real_mono_ee_interp_init(debugging_asked ? "" : opts);
}
