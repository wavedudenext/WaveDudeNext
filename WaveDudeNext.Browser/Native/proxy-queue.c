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

// The start that never came (2026-11-21; known since 2026-11-14 as "about one load in five never starts, without a
// word", the page's main thread at a whole core and answering nothing).
//
// The threaded runtime's C library (emscripten 3.1.56) hands a call from one thread to another through a queue of
// tasks a thread, kept in one structure under one lock (proxying.c, read in the SDK). Putting a task in (do_proxy)
// takes that lock and, the first time a thread is a target, makes that thread's queue under it, which allocates.
// On the page's main thread a lock that is not free is not slept on, a browser forbidding that there: the thread
// goes round a loop instead, and in that loop it carries out the tasks queued for itself
// (emscripten_proxy_execute_queue), so that a thread waiting for the main thread cannot hold it up. That function
// takes the queue's lock first.
//
// So: the main thread hands the runtime's first call to another thread; it holds the queue's lock and asks for
// memory; another thread happens to be inside the allocator at that instant, so the allocator's lock is taken; the
// main thread goes round its loop for it, the loop wants the queue's lock to look for its own tasks, and that lock
// is the main thread's own, taken a few frames further up. It waits for itself, for ever. The library guards against
// arriving there again from inside emscripten_proxy_execute_queue, not from inside do_proxy. Seen as it stood, by a
// sampling profiler on a page whose start was cut short by a second load (which makes the instant likelier; three of
// three such rounds hung within eleven loads): futex_wait_main_browser_thread under emscripten_proxy_execute_queue
// under the allocator's lock under get_or_add_tasks_for_thread under do_proxy, the call being the runtime's own
// first to its managed thread (mono_wasm_invoke_jsexport_async_post).
//
// The project file links the functions below in the library's place (-Wl,--wrap, as thread-id.c is). A thread that
// is putting a task in says so, and while it does, carrying out its own tasks first asks whether the queue's lock is
// free and lets it be when it is not: the lock is then this thread's own, or another thread's for an instant, and
// either way the loop that brought the thread here comes round again. Everywhere else the library's function runs
// as it is. To go when the runtime this head builds with has a C library with the fault mended.
#include <emscripten/proxying.h>
#include <pthread.h>

void __real_emscripten_proxy_execute_queue(em_proxying_queue *q);
int __real_emscripten_proxy_async(em_proxying_queue *q, pthread_t target, void (*func)(void *), void *arg);
int __real_emscripten_proxy_sync(em_proxying_queue *q, pthread_t target, void (*func)(void *), void *arg);
int __real_emscripten_proxy_sync_with_ctx(em_proxying_queue *q, pthread_t target, void (*func)(em_proxying_ctx *, void *), void *arg);
int __real_emscripten_proxy_callback(em_proxying_queue *q, pthread_t target, void (*func)(void *), void (*callback)(void *), void (*cancel)(void *), void *arg);
int __real_emscripten_proxy_callback_with_ctx(em_proxying_queue *q, pthread_t target, void (*func)(em_proxying_ctx *, void *), void (*callback)(void *),
	void (*cancel)(void *), void *arg);

// how many calls that put a task into a queue this thread is in (one inside another, when a task carried out
// meanwhile hands on a call of its own)
static _Thread_local int enqueuing;

void __wrap_emscripten_proxy_execute_queue(em_proxying_queue *q)
{
	if (enqueuing > 0)
	{
		// the queue's lock is the first member of its structure (struct em_proxying_queue in proxying.c)
		pthread_mutex_t *lock = (pthread_mutex_t *)q;
		if (pthread_mutex_trylock(lock) != 0)
		{
			return;
		}
		pthread_mutex_unlock(lock);
	}
	__real_emscripten_proxy_execute_queue(q);
}

int __wrap_emscripten_proxy_async(em_proxying_queue *q, pthread_t target, void (*func)(void *), void *arg)
{
	enqueuing++;
	int done = __real_emscripten_proxy_async(q, target, func, arg);
	enqueuing--;
	return done;
}

int __wrap_emscripten_proxy_sync(em_proxying_queue *q, pthread_t target, void (*func)(void *), void *arg)
{
	enqueuing++;
	int done = __real_emscripten_proxy_sync(q, target, func, arg);
	enqueuing--;
	return done;
}

int __wrap_emscripten_proxy_sync_with_ctx(em_proxying_queue *q, pthread_t target, void (*func)(em_proxying_ctx *, void *), void *arg)
{
	enqueuing++;
	int done = __real_emscripten_proxy_sync_with_ctx(q, target, func, arg);
	enqueuing--;
	return done;
}

int __wrap_emscripten_proxy_callback(em_proxying_queue *q, pthread_t target, void (*func)(void *), void (*callback)(void *), void (*cancel)(void *), void *arg)
{
	enqueuing++;
	int done = __real_emscripten_proxy_callback(q, target, func, callback, cancel, arg);
	enqueuing--;
	return done;
}

int __wrap_emscripten_proxy_callback_with_ctx(em_proxying_queue *q, pthread_t target, void (*func)(em_proxying_ctx *, void *), void (*callback)(void *),
	void (*cancel)(void *), void *arg)
{
	enqueuing++;
	int done = __real_emscripten_proxy_callback_with_ctx(q, target, func, callback, cancel, arg);
	enqueuing--;
	return done;
}
