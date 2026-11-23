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

// The browser's own H.264 encoder (WebCodecs), for WebCodecsH264Encoder: an MP4 export's frames go in as NV12 from
// the program's memory, and what the encoder makes of them waits here until the program takes it into its memory
// again. Only the calls a page's script must make are here; what is encoded how, and what the encoded bytes mean,
// is the program's.
//
// What waits is a row of records, each twelve bytes (three 32-bit numbers, the low byte first: the length of the
// bytes, the kind, the frame's number) and then the bytes. The kinds: 0 a frame, 1 a key frame, 2 the decoder's
// configuration (the avcC record of an MP4, which holds the parameter sets), which the browser hands over with the
// first frame.

const encoders = new Map();
let nextEncoder = 1;
const header = 12;

// the program's memory, shared between its threads; asked for afresh at every use, since it is another buffer after
// the memory has grown (main.js keeps the runtime's object for this)
const memory = () => globalThis.waveDudeRuntime.localHeapViewU8().buffer;

// The encoded stream in the form an MP4 stores (avc: the parameter sets apart, each unit behind its length); quality
// before speed, nobody watches while it is encoded.
function configOf(codec, width, height, framesPerSecond, bitrate) {
	return { codec, width, height, framerate: framesPerSecond, bitrate, avc: { format: 'avc' }, latencyMode: 'quality' };
}

function stateOf(number) {
	const state = encoders.get(number);
	if (!state) { throw new Error('the encoder is closed'); }
	if (state.error) { throw state.error; }
	return state;
}

function push(state, kind, index, bytes) {
	state.pending.push({ kind, index, bytes });
	state.pendingBytes += header + bytes.length;
}

// whether this browser encodes the codec (a string such as avc1.640028) at the size, rate and bitrate; false as
// well where it has no such interface at all
export async function supported(codec, width, height, framesPerSecond, bitrate) {
	if (typeof VideoEncoder === 'undefined' || typeof VideoFrame === 'undefined') { return false; }
	try {
		return (await VideoEncoder.isConfigSupported(configOf(codec, width, height, framesPerSecond, bitrate))).supported === true;
	} catch (error) {
		return false;
	}
}

// an encoder for a configuration that is supported; answers its number
export function open(codec, width, height, framesPerSecond, bitrate) {
	const state = { width, height, framesPerSecond, pending: [], pendingBytes: 0, error: undefined, wake: undefined };
	state.encoder = new VideoEncoder({
		output: (chunk, metadata) => {
			const description = metadata && metadata.decoderConfig && metadata.decoderConfig.description;
			if (description) {
				push(state, 2, 0, ArrayBuffer.isView(description)
					? new Uint8Array(description.buffer, description.byteOffset, description.byteLength)
					: new Uint8Array(description));
			}
			const bytes = new Uint8Array(chunk.byteLength);
			chunk.copyTo(bytes);
			push(state, chunk.type === 'key' ? 1 : 0, Math.round(chunk.timestamp * framesPerSecond / 1e6), bytes);
		},
		error: error => {
			state.error = error;
			if (state.wake) { state.wake(); }
		},
	});
	state.encoder.configure(configOf(codec, width, height, framesPerSecond, bitrate));
	const number = nextEncoder++;
	encoders.set(number, state);
	return number;
}

// resolves when the encoder has taken a frame out of its queue, or has failed
function room(state) {
	return new Promise(resolve => {
		const done = () => {
			state.encoder.removeEventListener('dequeue', done);
			state.wake = undefined;
			resolve();
		};
		state.wake = done;
		state.encoder.addEventListener('dequeue', done);
	});
}

// The frame, length bytes of NV12 in the program's memory at address, is given to the encoder; resolves to the bytes
// of the records that wait. No more than a few frames wait in the encoder: the program's thread waits here instead,
// its frame held in place meanwhile. The browser copies the frame as it is made, so the memory is the program's
// again when this resolves. The colours are the planes': BT.601, studio range.
export async function encode(number, address, length, index, key) {
	let state = stateOf(number);
	while (state.encoder.encodeQueueSize > 2) {
		await room(state);
		state = stateOf(number);
	}
	const frame = new VideoFrame(new Uint8Array(memory(), address >>> 0, length), {
		format: 'NV12',
		codedWidth: state.width,
		codedHeight: state.height,
		timestamp: Math.round(index * 1e6 / state.framesPerSecond),
		duration: Math.round(1e6 / state.framesPerSecond),
		colorSpace: { primaries: 'smpte170m', transfer: 'smpte170m', matrix: 'smpte170m', fullRange: false },
	});
	try {
		state.encoder.encode(frame, { keyFrame: key });
	} finally {
		frame.close();
	}
	return state.pendingBytes;
}

// The records that wait, as many whole ones as fit, into capacity bytes of the program's memory at address; answers
// the bytes written, or, where not even the first fits, the room it needs as a negative number.
export function take(number, address, capacity) {
	const state = stateOf(number);
	const target = new Uint8Array(memory(), address >>> 0, capacity);
	const numbers = new DataView(target.buffer, target.byteOffset, capacity);
	let at = 0;
	while (state.pending.length > 0) {
		const record = state.pending[0];
		const size = header + record.bytes.length;
		if (at + size > capacity) { return at > 0 ? at : -size; }
		numbers.setInt32(at, record.bytes.length, true);
		numbers.setInt32(at + 4, record.kind, true);
		numbers.setInt32(at + 8, record.index, true);
		target.set(record.bytes, at + header);
		at += size;
		state.pending.shift();
		state.pendingBytes -= size;
	}
	return at;
}

// no more frames come: resolves, once the encoder has made the last of them, to the bytes of the records that wait
export async function finish(number) {
	const state = stateOf(number);
	await state.encoder.flush();
	return stateOf(number).pendingBytes;
}

// the encoder is given back, whatever its state
export function close(number) {
	const state = encoders.get(number);
	if (!state) { return; }
	encoders.delete(number);
	if (state.encoder.state !== 'closed') { state.encoder.close(); }
}
