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

// The device side of the browser's GPU engine (WebGpuWaveSimulator.cs calls every export here, from the one thread
// the page's scripts are called from): the kernels of the OpenCL engine, GpuWaveSimulator.cs, in WGSL, kernel for
// kernel and line for line where the languages allow, on the same padded layout (a border of one cell, the cell of
// (x, y) at (y + 1) * stride + x + 1) and the same tables, which the library's GpuTables makes for both engines.
// What differs from OpenCL C: WGSL has no 8- and 16-bit storage, so the scene's index maps, the captured halves and
// the palette indices are packed into 32-bit words (little-endian, as the bytes lie in memory), and a capture's work
// item is one word of its output; a source's or a probe's per-element tables are one array of structures, since a
// stage takes eight storage buffers. One engine object per engine of the program, all on one device.

let device = null;
let description = '';
let detachRamp = 30;
let edgeSpeedFactor = 0.56213877;
const engines = new Map();
let nextEngine = 1;
const pipelines = new Map();

// ---- the kernels

const indexMaps = `
fn c2_of(i: i32) -> f32 { return c2t[(c2i[u32(i) >> 2u] >> ((u32(i) & 3u) * 8u)) & 0xFFu]; }`;

// wave_step: one leapfrog sub-step, one work item per interior cell; see GpuWaveSimulator.KernelSource
const stepShader = fourth => `
struct Params { w: i32, h: i32, stride: i32, track: i32, c2Scale: f32, alpha: f32, pad0: f32, pad1: f32 };
@group(0) @binding(0) var<uniform> p: Params;
@group(0) @binding(1) var<storage, read> u: array<f32>;
@group(0) @binding(2) var<storage, read_write> up: array<f32>;
@group(0) @binding(3) var<storage, read> c2i: array<u32>;
@group(0) @binding(4) var<storage, read> di: array<u32>;
@group(0) @binding(5) var<storage, read> c2t: array<f32>;
@group(0) @binding(6) var<storage, read> dt: array<f32>;
@group(0) @binding(7) var<storage, read_write> inten1: array<f32>;
@group(0) @binding(8) var<storage, read_write> inten: array<f32>;
${indexMaps}
const FOURTH: bool = ${fourth};
@compute @workgroup_size(32, 8)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
	let x = i32(id.x);
	let y = i32(id.y);
	if (x >= p.w || y >= p.h) { return; }
	let s = p.stride;
	let i = (y + 1) * s + x + 1;
	let uc = u[i];
	var lap: f32;
	if (FOURTH && x >= 1 && x <= p.w - 2 && y >= 1 && y <= p.h - 2) {
		lap = (-1.0 / 12.0) * (u[i - 2] + u[i + 2] + u[i - 2 * s] + u[i + 2 * s])
			+ (4.0 / 3.0) * (u[i - 1] + u[i + 1] + u[i - s] + u[i + s])
			- 5.0 * uc;
	} else {
		lap = 0.066 * (u[i - s - 1] + u[i - s + 1] + u[i + s - 1] + u[i + s + 1])
			+ 0.184 * (u[i - 1] + u[i + 1] + u[i - s] + u[i + s]) - uc;
	}
	let d = dt[(di[u32(i) >> 1u] >> ((u32(i) & 1u) * 16u)) & 0xFFFFu];
	let nv = uc + (uc - up[i]) * d + lap * c2_of(i) * p.c2Scale;
	up[i] = nv;
	if (p.track != 0) {
		let s1 = inten1[i] * p.alpha + nv * nv * (1.0 - p.alpha);
		inten1[i] = s1;
		inten[i] = inten[i] * p.alpha + s1 * (1.0 - p.alpha);
	}
}`;

// open_edges: Mur's first-order absorbing condition on the ring, one work item per column of the top and bottom rows
// (corners take their diagonal neighbour), then one per row of the left and right columns
const edgeShader = () => `
struct Params { w: i32, h: i32, stride: i32, pad: i32, c2Scale: f32, pad0: f32, pad1: f32, pad2: f32 };
@group(0) @binding(0) var<uniform> p: Params;
@group(0) @binding(1) var<storage, read> u: array<f32>;
@group(0) @binding(2) var<storage, read_write> up: array<f32>;
@group(0) @binding(3) var<storage, read> c2i: array<u32>;
@group(0) @binding(5) var<storage, read> c2t: array<f32>;
${indexMaps}
fn absorb(ring: i32, inner: i32) {
	let s = sqrt(c2_of(inner) * p.c2Scale) * ${edgeSpeedFactor};
	let k = (s - 1.0) / (s + 1.0);
	up[ring] = u[inner] + k * (up[inner] - u[ring]);
}
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
	let n = i32(id.x);
	if (n < p.w + 2) {
		let ix = clamp(n, 1, p.w);
		absorb(n, p.stride + ix);
		absorb((p.h + 1) * p.stride + n, p.h * p.stride + ix);
	} else {
		let y = n - (p.w + 2) + 1;
		if (y > p.h) { return; }
		absorb(y * p.stride, y * p.stride + 1);
		absorb(y * p.stride + p.w + 1, y * p.stride + p.w);
	}
}`;

// apply_sources: the image sources, u = 0.9·u + 0.1·sin(freq[level]·t)·amp
const imageSourceShader = () => `
struct Params { t: f32, amp: f32, n: i32, pad: i32 };
@group(0) @binding(0) var<uniform> p: Params;
@group(0) @binding(1) var<storage, read_write> u: array<f32>;
@group(0) @binding(2) var<storage, read> src: array<vec2<i32>>;
@group(0) @binding(3) var<storage, read> freq: array<f32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
	let k = i32(id.x);
	if (k >= p.n) { return; }
	let cell = src[k].x;
	u[cell] = u[cell] * 0.9 + sin(freq[src[k].y] * p.t) * p.amp * 0.1;
}`;

// apply_object_sources: one element per work item; the off-width, the cycle anchor and the random state are the
// kernel's own from one step to the next (an element that re-randomizes its off-width redraws it here). A launch
// covers one pass of the table, the elements from first up to n: within a pass no two elements drive the same cell
// (see setEmitters)
const objectSourceShader = () => `
struct Params { t: f32, n: i32, first: i32, pad1: i32 };
struct Source {
	idx: i32, omega: f32, amp: f32, phase: f32,
	mode: i32, pulseShape: i32, onWidth: i32, offWidth: i32,
	detachStep: i32, offWidthBase: i32, randomize: i32, cycleAnchor: f32,
	rngState: u32, pad0: u32, pad1: u32, pad2: u32,
};
@group(0) @binding(0) var<uniform> p: Params;
@group(0) @binding(1) var<storage, read_write> u: array<f32>;
@group(0) @binding(2) var<storage, read_write> src: array<Source>;
const PI: f32 = 3.14159274;
fn xorshift32(seed: u32) -> u32 {
	var x = seed;
	x ^= x << 13u;
	x ^= x >> 17u;
	x ^= x << 5u;
	return x;
}
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
	let k = i32(id.x) + p.first;
	if (k >= p.n) { return; }
	let t = p.t;
	var detachFade = 1.0;
	if (src[k].detachStep >= 0) {
		let stepsSinceDetach = t - f32(src[k].detachStep);
		if (stepsSinceDetach >= ${detachRamp}.0) { return; }
		if (stepsSinceDetach > 0.0) { detachFade = 1.0 - stepsSinceDetach / ${detachRamp}.0; }
	}
	let f = src[k].omega;
	var envelope = 1.0;
	if (src[k].mode != 0) {
		var ow = src[k].offWidth;
		let onWidth = src[k].onWidth;
		var period = onWidth + ow;
		if (period > 0) {
			let cycleClock = f * t / (2.0 * PI);
			var pos: f32;
			var isFirstCycle: bool;
			if (src[k].randomize <= 0) {
				let adjustedClock = cycleClock + f32(ow);
				isFirstCycle = floor(adjustedClock / f32(period)) == 0.0;
				pos = adjustedClock - f32(period) * floor(adjustedClock / f32(period));
			} else {
				pos = cycleClock - src[k].cycleAnchor;
				var guard = 0;
				while (pos >= f32(period) && guard < 1000) {
					src[k].cycleAnchor += f32(period);
					pos -= f32(period);
					let state = xorshift32(src[k].rngState);
					src[k].rngState = state;
					let amt = src[k].randomize;
					let span = 2 * amt + 1;
					let r = i32(state % u32(span)) - amt;
					ow = src[k].offWidthBase + r;
					if (ow < 1) { ow = 1; }
					src[k].offWidth = ow;
					period = onWidth + ow;
					guard++;
				}
				if (pos < 0.0) { pos = 0.0; }
				isFirstCycle = src[k].cycleAnchor == 0.0;
			}
			if (src[k].pulseShape == 0) {
				if (pos < f32(ow)) {
					envelope = 0.0;
				} else {
					let onPos = pos - f32(ow);
					let center = f32(onWidth) / 2.0;
					let sigma = max(f32(onWidth) / 6.0, 1.0e-6);
					let dd = (onPos - center) / sigma;
					envelope = exp(-0.5 * dd * dd);
				}
			} else {
				var shifted = pos - f32(ow);
				if (shifted < -0.5 * f32(ow) && !isFirstCycle) { shifted += f32(period); }
				let edgeWidth = 0.35;
				let rising = 0.5 + atan(shifted / edgeWidth) / PI;
				let falling = 0.5 - atan((shifted - f32(onWidth)) / edgeWidth) / PI;
				envelope = rising * falling;
			}
		}
	}
	let v = sin(src[k].phase + f * t) * src[k].amp * envelope * detachFade;
	let cell = src[k].idx;
	u[cell] = u[cell] * 0.9 + v * 0.1;
}`;

// capture_half / capture_index: the canvas of a padded float buffer as a tightly packed plane of the frame store's
// shape, every div × div block of cells averaged into one sample, stored as a half or as the palette index the
// renderer would compute at the given brightness. One work item per 32-bit word of the plane: two halves or four
// index bytes. src2 with a blend scale above 0 is the intensity's second stage (the plane is their blend, as the
// CPU engine's output); kind: for halves 1 = store the square root; for indices 0 the field, 1 the intensity, 2 its root
const captureShader = index => `
struct Params { w: i32, h: i32, stride: i32, div: i32, fw: i32, fh: i32, rim: i32, kind: i32, brightness: f32, blendScale: f32, pad0: f32, pad1: f32 };
@group(0) @binding(0) var<uniform> p: Params;
@group(0) @binding(1) var<storage, read> src: array<f32>;
@group(0) @binding(2) var<storage, read> src2: array<f32>;
@group(0) @binding(3) var<storage, read_write> dst: array<u32>;
fn cell_value(j: i32) -> f32 {
	if (p.blendScale > 0.0) { return (src[j] * 0.5 + src2[j] * 0.5) * p.blendScale; }
	return src[j];
}
fn sample_at(x: i32, y: i32) -> f32 {
	if (p.div == 1) { return cell_value((y + 1 + p.rim) * p.stride + x + 1 + p.rim); }
	let x0 = x * p.div;
	let y0 = y * p.div;
	let x1 = min(p.w, x0 + p.div);
	let y1 = min(p.h, y0 + p.div);
	var sum = 0.0;
	for (var cy = y0; cy < y1; cy++) {
		for (var cx = x0; cx < x1; cx++) {
			sum += cell_value((cy + 1 + p.rim) * p.stride + cx + 1 + p.rim);
		}
	}
	return sum / f32((y1 - y0) * (x1 - x0));
}
// the bits of the half nearest to f, ties to even, half's subnormals and overflow included
fn half_bits(f: f32) -> u32 {
	let bits = bitcast<u32>(f);
	let sign = (bits >> 16u) & 0x8000u;
	let a = bits & 0x7FFFFFFFu;
	if (a > 0x7F800000u) { return sign | 0x7E00u; }
	if (a >= 0x477FF000u) { return sign | 0x7C00u; }
	if (a < 0x38800000u) { return sign | u32(round(bitcast<f32>(a) * 16777216.0)); }
	var h = (((a >> 23u) - 112u) << 10u) | ((a & 0x7FFFFFu) >> 13u);
	let rest = a & 0x1FFFu;
	if (rest > 0x1000u || (rest == 0x1000u && (h & 1u) == 1u)) { h += 1u; }
	return sign | h;
}
// f rounded to the half a 16-bit frame stores, as a float: the index is that of exactly that half
fn round_to_half(f: f32) -> f32 {
	let bits = bitcast<u32>(f);
	let sign = bits & 0x80000000u;
	let a = bits & 0x7FFFFFFFu;
	if (a >= 0x7F800000u) { return f; }
	if (a >= 0x477FF000u) { return bitcast<f32>(sign | 0x7F800000u); }
	if (a < 0x38800000u) {
		let q = round(bitcast<f32>(a) * 16777216.0) * 5.9604644775390625e-8;
		return bitcast<f32>(sign | bitcast<u32>(q));
	}
	let rest = a & 0x1FFFu;
	var kept = a & 0xFFFFE000u;
	if (rest > 0x1000u || (rest == 0x1000u && (kept & 0x2000u) != 0u)) { kept += 0x2000u; }
	return bitcast<f32>(sign | kept);
}
fn index_of(raw: f32) -> u32 {
	var v: f32;
	if (p.kind == 2) { v = round_to_half(sqrt(raw)); } else { v = round_to_half(raw); }
	if (p.kind == 2) {
		if (v > 0.0) { return u32(min(v * p.brightness, 1.0) * 254.0); }
		return 0u;
	}
	if (p.kind == 1) {
		if (v > 0.0) { return u32(min(sqrt(v) * p.brightness, 1.0) * 254.0); }
		return 0u;
	}
	let s = v * p.brightness;
	if (s != s) { return 127u; }
	return u32(clamp(s, -1.0, 1.0) * 127.0 + 127.0);
}
const PER_WORD: i32 = ${index ? 4 : 2};
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
	let total = p.fw * p.fh;
	let first = i32(id.x) * PER_WORD;
	if (first >= total) { return; }
	var word = 0u;
	for (var k = 0; k < PER_WORD; k++) {
		let n = first + k;
		if (n >= total) { break; }
		let raw = sample_at(n % p.fw, n / p.fw);
		${index
		? 'word |= index_of(raw) << (u32(k) * 8u);'
		: 'var v = raw; if (p.kind != 0) { v = sqrt(raw); } word |= half_bits(v) << (u32(k) * 16u);'}
	}
	dst[id.x] = word;
}`;

// probe_sample: the value of every point probe into row "row" of the history, one work item per probe
const probeShader = () => `
struct Params { row: i32, n: i32, scale: f32, pad: f32 };
@group(0) @binding(0) var<uniform> p: Params;
@group(0) @binding(1) var<storage, read> u: array<f32>;
@group(0) @binding(2) var<storage, read> inten1: array<f32>;
@group(0) @binding(3) var<storage, read> inten: array<f32>;
@group(0) @binding(4) var<storage, read> pix: array<i32>;
@group(0) @binding(5) var<storage, read> info: array<vec4<i32>>;
@group(0) @binding(6) var<storage, read_write> hist: array<f32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
	let k = i32(id.x);
	if (k >= p.n) { return; }
	let start = info[k].x;
	let count = info[k].y;
	var s = 0.0;
	if (info[k].z != 0) {
		for (var j = 0; j < count; j++) { let cell = pix[start + j]; s += (inten1[cell] * 0.5 + inten[cell] * 0.5) * p.scale; }
	} else {
		for (var j = 0; j < count; j++) { s += u[pix[start + j]]; }
	}
	if (info[k].w != 0 && count > 0) { s = s / f32(count); }
	hist[p.row * p.n + k] = s;
}`;

// profile_accumulate and profile_gather: the line probes, one work item per line pixel; a pixel is (cell, mode,
// start flag, start step), mode 0 the field, 1 the intensity, 2 the intensity's total over the steps
const profileShader = gather => `
struct Params { step: i32, n: i32, scale: f32, pad: f32 };
@group(0) @binding(0) var<uniform> p: Params;
@group(0) @binding(1) var<storage, read> u: array<f32>;
@group(0) @binding(2) var<storage, read> inten1: array<f32>;
@group(0) @binding(3) var<storage, read> inten: array<f32>;
@group(0) @binding(4) var<storage, read> pp: array<vec4<i32>>;
@group(0) @binding(5) var<storage, read_write> total: array<f32>;
@group(0) @binding(6) var<storage, read_write> outv: array<f32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
	let k = i32(id.x);
	if (k >= p.n) { return; }
	let cell = pp[k].x;
	let mode = pp[k].y;
	${gather
		? `var v: f32;
	if (mode == 2) { v = total[k]; }
	else if (mode == 1) { v = (inten1[cell] * 0.5 + inten[cell] * 0.5) * p.scale; }
	else { v = u[cell]; }
	outv[k] = v;`
		: `if (mode != 2) { return; }
	if (pp[k].z != 0 && p.step < pp[k].w) { return; }
	total[k] += (inten1[cell] * 0.5 + inten[cell] * 0.5) * p.scale;`}
}`;

function pipeline(name, source) {
	let made = pipelines.get(name);
	if (!made) {
		made = device.createComputePipeline({ layout: 'auto', compute: { module: device.createShaderModule({ code: source() }), entryPoint: 'main' } });
		pipelines.set(name, made);
	}
	return made;
}

// ---- the device

// Finds the adapter and makes the device, once; the adapter's name and limits for the log. Throws where the browser has no WebGPU.
export async function init(detachRampSteps, edgeFactor) {
	if (device) { return description; }
	detachRamp = detachRampSteps;
	edgeSpeedFactor = edgeFactor;
	if (!navigator.gpu) { throw new Error('this browser has no WebGPU'); }
	const adapter = await navigator.gpu.requestAdapter();
	if (!adapter) { throw new Error('WebGPU found no graphics adapter'); }
	const limits = adapter.limits;
	device = await adapter.requestDevice({
		requiredLimits: { maxStorageBufferBindingSize: limits.maxStorageBufferBindingSize, maxBufferSize: limits.maxBufferSize, maxStorageBuffersPerShaderStage: Math.min(limits.maxStorageBuffersPerShaderStage, 10) },
	});
	device.addEventListener('uncapturederror', event => console.error('WebGPU: ' + event.error.message));
	device.lost.then(info => console.error('WebGPU: the device was lost (' + info.reason + '): ' + info.message));
	const info = adapter.info || {};
	const name = [info.vendor, info.architecture, info.device, info.description].filter(part => part).join(' ') || 'unnamed adapter';
	const l = device.limits;
	// divided, not shifted: a shift reckons in 32 signed bits, and a buffer limit of 2 GiB came out as -2048
	const mebibytes = bytes => Math.floor(bytes / 1048576);
	description = name + '|' + `${name}: storage binding ${mebibytes(l.maxStorageBufferBindingSize)} MiB, buffer ${mebibytes(l.maxBufferSize)} MiB, ` +
		`work groups ${l.maxComputeWorkgroupSizeX} × ${l.maxComputeWorkgroupSizeY}, ${l.maxComputeInvocationsPerWorkgroup} invocations, ` +
		`${l.maxStorageBuffersPerShaderStage} storage buffers a stage, ${l.maxComputeWorkgroupsPerDimension} groups a dimension`;
	return description;
}

// the largest padded grid, in cells, one storage binding holds
export function maxCells() { return Math.floor(device.limits.maxStorageBufferBindingSize / 4); }

const storageUsage = () => GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC | GPUBufferUsage.COPY_DST;
const pad4 = bytes => Math.max(4, (bytes + 3) & ~3);

function storage(bytes, data) {
	const buffer = device.createBuffer({ size: pad4(bytes), usage: storageUsage() });
	if (data && data.length > 0) { write(buffer, data); }
	return buffer;
}

// a typed array into a buffer, padded to whole words (writeBuffer wants a multiple of four bytes)
function write(buffer, data) {
	const bytes = new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
	if (bytes.length % 4 === 0) { device.queue.writeBuffer(buffer, 0, bytes); return; }
	const padded = new Uint8Array(pad4(bytes.length));
	padded.set(bytes);
	device.queue.writeBuffer(buffer, 0, padded);
}

function uniform(bytes) { return device.createBuffer({ size: bytes, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST }); }

function group(made, buffers) {
	const entries = [];
	buffers.forEach((buffer, binding) => { if (buffer) { entries.push({ binding, resource: { buffer } }); } });
	return device.createBindGroup({ layout: made.getBindGroupLayout(0), entries });
}

function pass(encoder, made, bindGroup, x, y) {
	const computePass = encoder.beginComputePass();
	computePass.setPipeline(made);
	computePass.setBindGroup(0, bindGroup);
	computePass.dispatchWorkgroups(x, y || 1);
	computePass.end();
}

// the program's memory as bytes, asked for afresh at every use: the view goes stale when the memory grows (main.js
// keeps the runtime's object for this)
const heap = () => globalThis.waveDudeRuntime.localHeapViewU8();

// ---- an engine

// The buffers and bind groups of one engine. The views are copied before this returns (they are valid during the call
// alone). Returns the engine's number.
export function create(w, h, stride, rim, canvasWidth, canvasHeight, fourth, track, openEdges, subSteps, c2Scale, alpha, intensityScale, sourceAmplitude,
	sourceCount, dTableCapacity, c2Index, dIndex, c2Table, dTable, frequencies, imageSources) {
	const cells = stride * (h + 2);
	const e = {
		w, h, stride, rim, canvasWidth, canvasHeight, fourth, track, openEdges, subSteps, c2Scale, alpha, intensityScale, sourceAmplitude, sourceCount, cells,
		dTableCapacity, current: 0, time: 0, objectCount: 0, objectPasses: [], captures: new Map(), nextCapture: 1, readPool: new Map(), probes: null,
		// steps sent to the card, and how many of them it is known to have done (see run)
		submitted: 0, done: 0,
	};
	e.field = [storage(cells * 4), storage(cells * 4)];
	// not tracked: the kernels still need valid bindings, and they never touch them
	e.inten1 = storage(track ? cells * 4 : 4);
	e.inten = storage(track ? cells * 4 : 4);
	e.c2Index = storage(cells, c2Index.slice());
	e.dIndex = storage(cells * 2, dIndex.slice());
	e.c2Table = storage(256 * 4, c2Table.slice());
	e.dTable = storage(dTableCapacity * 4, dTable.slice());
	e.frequencies = storage(256 * 4, frequencies.slice());
	e.imageSources = storage(Math.max(8, sourceCount * 8), sourceCount > 0 ? imageSources.slice() : null);
	e.stepParams = [uniform(32), uniform(32)];
	e.edgeParams = uniform(32);
	e.imageParams = uniform(16);
	writeStepParams(e);
	const step = pipeline('step' + fourth, () => stepShader(fourth));
	// stepGroups[newest][track]: reads the newest field, overwrites the other
	e.stepGroups = [0, 1].map(newest => [0, 1].map(trackNow => group(step, [e.stepParams[trackNow], e.field[newest], e.field[1 - newest], e.c2Index, e.dIndex, e.c2Table, e.dTable, e.inten1, e.inten])));
	if (openEdges) {
		const edges = pipeline('edges', edgeShader);
		e.edgeGroups = [0, 1].map(newest => group(edges, [e.edgeParams, e.field[newest], e.field[1 - newest], e.c2Index, null, e.c2Table]));
	}
	if (sourceCount > 0) {
		const image = pipeline('image', imageSourceShader);
		e.imageGroups = [0, 1].map(newest => group(image, [e.imageParams, e.field[newest], e.imageSources, e.frequencies]));
	}
	// the captures' staging planes: at most the whole canvas as halves
	const planeBytes = pad4(canvasWidth * canvasHeight * 2);
	e.staging = [storage(planeBytes), track ? storage(planeBytes) : null];
	e.captureParams = [uniform(48), uniform(48)];
	const number = nextEngine++;
	engines.set(number, e);
	return number;
}

function writeStepParams(e) {
	[0, 1].forEach(trackNow => {
		const data = new ArrayBuffer(32);
		new Int32Array(data, 0, 4).set([e.w, e.h, e.stride, trackNow && e.track ? 1 : 0]);
		new Float32Array(data, 16, 2).set([e.c2Scale, e.alpha]);
		device.queue.writeBuffer(e.stepParams[trackNow], 0, data);
	});
	const edge = new ArrayBuffer(32);
	new Int32Array(edge, 0, 3).set([e.w, e.h, e.stride]);
	new Float32Array(edge, 16, 1)[0] = e.c2Scale;
	device.queue.writeBuffer(e.edgeParams, 0, edge);
}

// another scene of the same grid: the maps, the tables and the image sources; the engine is reset by the caller
export function setScene(number, subSteps, c2Scale, c2Index, dIndex, c2Table, dTable, imageSources) {
	const e = engines.get(number);
	e.subSteps = subSteps;
	e.c2Scale = c2Scale;
	write(e.c2Index, c2Index.slice());
	write(e.dIndex, dIndex.slice());
	write(e.c2Table, c2Table.slice());
	write(e.dTable, dTable.slice());
	if (e.sourceCount > 0) { write(e.imageSources, imageSources.slice()); }
	writeStepParams(e);
}

// the object sources' table (16 words an element, see the kernel's structure), the passes it is applied in, and the
// intensity's retention, which follows the lowest source level in use. The passes (the library's GpuTables.Passes,
// 2026-11-19): the elements of one launch run at once, each reading its cell and writing it back, so two that drive
// the same cell must not share a launch, or one's drive is lost. The table comes ordered by pass, passEnds holding
// for every pass the count of elements up to its end as 32-bit numbers; nearly every project has one pass. A pass has
// a uniform buffer of its own, since the buffers written before a submit hold their last values for all of it.
export function setEmitters(number, count, table, alpha, passEnds) {
	const e = engines.get(number);
	if (e.objectSources) { e.objectSources.destroy(); }
	for (const objects of e.objectPasses) { objects.params.destroy(); }
	e.objectCount = count;
	e.objectSources = storage(Math.max(64, count * 64), count > 0 ? table.slice() : null);
	const made = pipeline('object', objectSourceShader);
	const ends = count > 0 ? passEnds.slice() : new Uint8Array(0);
	const numbers = new Int32Array(ends.buffer, ends.byteOffset, ends.byteLength >> 2);
	e.objectPasses = [];
	let first = 0;
	for (const end of numbers) {
		const params = uniform(16);
		e.objectPasses.push({ first, end, params, groups: [0, 1].map(newest => group(made, [params, e.field[newest], e.objectSources])) });
		first = end;
	}
	e.alpha = alpha;
	writeStepParams(e);
}

// back to step 0: the fields and the intensity cleared; the emitters' state is set again by the caller
export function reset(number) {
	const e = engines.get(number);
	const encoder = device.createCommandEncoder();
	for (const buffer of [e.field[0], e.field[1], e.inten1, e.inten]) { encoder.clearBuffer(buffer); }
	device.queue.submit([encoder.finish()]);
	e.current = 0;
	e.time = 0;
	setProbes(number, 0, 0, null, null, 0, null, false);
}

function step(e) {
	const encoder = device.createCommandEncoder();
	if (e.sourceCount > 0) {
		const data = new ArrayBuffer(16);
		new Float32Array(data, 0, 2).set([e.time, e.sourceAmplitude]);
		new Int32Array(data, 8, 1)[0] = e.sourceCount;
		device.queue.writeBuffer(e.imageParams, 0, data);
		pass(encoder, pipeline('image'), e.imageGroups[e.current], Math.ceil(e.sourceCount / 64));
	}
	// a launch per pass, one after the other, each over its own stretch of the table
	for (const objects of e.objectPasses) {
		const data = new ArrayBuffer(16);
		new Float32Array(data, 0, 1)[0] = e.time;
		new Int32Array(data, 4, 2).set([objects.end, objects.first]);
		device.queue.writeBuffer(objects.params, 0, data);
		pass(encoder, pipeline('object'), objects.groups[e.current], Math.ceil((objects.end - objects.first) / 64));
	}
	const made = pipeline('step' + e.fourth);
	for (let sub = 1; sub <= e.subSteps; sub++) {
		// only the last sub-step updates the intensity, so its averaging runs once a step
		pass(encoder, made, e.stepGroups[e.current][sub === e.subSteps ? 1 : 0], Math.ceil(e.w / 32), Math.ceil(e.h / 8));
		if (e.openEdges) { pass(encoder, pipeline('edges'), e.edgeGroups[e.current], Math.ceil((e.w + 2 + e.h) / 64)); }
		e.current = 1 - e.current;
	}
	device.queue.submit([encoder.finish()]);
	e.time += 1;
}

// steps sent without a wait for the card after which run waits for its "done"
const waitEvery = 256;

// the operations the engine gathered since its last call, in their order: 0 a step, 1 the probes sampled
async function perform(e, operations) {
	for (const operation of operations) {
		if (operation === 0) { step(e); e.submitted++; } else { await sample(e); }
	}
}

// Sends the gathered operations. The card is not waited for: its "done" takes about 3 ms to arrive whatever the work
// (Edge 157, measured 2026-11-13: a step sent and waited for singly took 3.3 ms against 0.3), and a read or a capture
// awaited later waits for everything before it anyway. Only when waitEvery steps have gone out with no such wait is
// the card waited for, so the commands do not pile up.
export async function run(number, operations) {
	const e = engines.get(number);
	await perform(e, operations.slice());
	if (e.submitted - e.done >= waitEvery) {
		const mark = e.submitted;
		await device.queue.onSubmittedWorkDone();
		e.done = Math.max(e.done, mark);
	}
}

// ---- reading back

function readBuffer(e, bytes) {
	const size = pad4(bytes);
	const pool = e.readPool.get(size);
	if (pool && pool.length > 0) { return pool.pop(); }
	return device.createBuffer({ size, usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST });
}

function release(e, buffer) {
	buffer.unmap();
	const pool = e.readPool.get(buffer.size) || [];
	if (pool.length < 4) { pool.push(buffer); e.readPool.set(buffer.size, pool); } else { buffer.destroy(); }
}

// bytes of a storage buffer into the program's memory at address (an address arrives as a signed number: above 2 GB
// it would be a negative one, hence the >>> 0 wherever one is used)
async function download(e, source, bytes, address) {
	address >>>= 0;
	const target = readBuffer(e, bytes);
	const encoder = device.createCommandEncoder();
	encoder.copyBufferToBuffer(source, 0, target, 0, pad4(bytes));
	device.queue.submit([encoder.finish()]);
	const mark = e.submitted;
	await target.mapAsync(GPUMapMode.READ, 0, pad4(bytes));
	e.done = Math.max(e.done, mark);
	heap().set(new Uint8Array(target.getMappedRange(0, pad4(bytes)), 0, bytes), address);
	release(e, target);
}

// the padded floats of the newest field (0), the intensity's first stage (1) or its second (2)
export async function read(number, what, address, bytes) {
	const e = engines.get(number);
	await download(e, what === 0 ? e.field[e.current] : what === 1 ? e.inten1 : e.inten, bytes, address);
}

// A capture, after the operations gathered before it (as run's, in the same call, which spares a hand-over between
// the threads): the canvas converted on the card into a plane of the shape (halves or palette indices), copied into a
// buffer the page can read, and the reading begun; the card is not waited for. Resolves to the capture's ticket for
// awaitCapture.
export async function capture(number, operations, index, divisor, width, height, withIntensity, root, fieldBrightness, intensityBrightness) {
	const e = engines.get(number);
	await perform(e, operations.slice());
	const made = pipeline('capture' + index, () => captureShader(index));
	const samples = width * height;
	const bytes = samples * (index ? 1 : 2);
	const planes = withIntensity ? [false, true] : [false];
	const encoder = device.createCommandEncoder();
	const targets = planes.map(intensity => {
		const slot = intensity ? 1 : 0;
		const data = new ArrayBuffer(48);
		// kind: halves 1 = the square root; indices 0 the field, 1 the intensity, 2 its root
		const kind = index ? (intensity ? (root ? 2 : 1) : 0) : (intensity && root ? 1 : 0);
		new Int32Array(data, 0, 8).set([e.canvasWidth, e.canvasHeight, e.stride, divisor, width, height, e.rim, kind]);
		new Float32Array(data, 32, 2).set([intensity ? intensityBrightness : fieldBrightness, intensity ? e.intensityScale : 0]);
		device.queue.writeBuffer(e.captureParams[slot], 0, data);
		const source = intensity ? e.inten1 : e.field[e.current];
		const bindGroup = group(made, [e.captureParams[slot], source, intensity ? e.inten : source, e.staging[slot]]);
		pass(encoder, made, bindGroup, Math.ceil(Math.ceil(samples / (index ? 4 : 2)) / 256));
		const target = readBuffer(e, bytes);
		encoder.copyBufferToBuffer(e.staging[slot], 0, target, 0, pad4(bytes));
		return target;
	});
	device.queue.submit([encoder.finish()]);
	const ticket = e.nextCapture++;
	e.captures.set(ticket, { bytes, targets, mark: e.submitted, mapped: targets.map(target => target.mapAsync(GPUMapMode.READ, 0, pad4(bytes))) });
	return ticket;
}

// waits for a capture's planes and copies them into the snapshot's memory
export async function awaitCapture(number, ticket, fieldAddress, intensityAddress) {
	const e = engines.get(number);
	const taken = e.captures.get(ticket);
	e.captures.delete(ticket);
	await Promise.all(taken.mapped);
	e.done = Math.max(e.done, taken.mark);
	const addresses = [fieldAddress >>> 0, intensityAddress >>> 0];
	taken.targets.forEach((target, plane) => {
		heap().set(new Uint8Array(target.getMappedRange(0, pad4(taken.bytes)), 0, taken.bytes), addresses[plane]);
		release(e, target);
	});
}

// ---- the probes

// count probes; the point probes' pixel list and the four ints a probe (all 0 for a line); the line probes' pixels,
// four ints each. count 0 removes the probes.
export function setProbes(number, count, seriesPixels, pixels, info, profileCount, profilePixels, integrates) {
	const e = engines.get(number);
	if (e.probes) {
		for (const buffer of [e.probes.pixels, e.probes.info, e.probes.history, e.probes.profile, e.probes.total, e.probes.out, e.probes.params, e.probes.profileParams]) { if (buffer) { buffer.destroy(); } }
	}
	e.probes = null;
	if (count === 0) { return; }
	const probes = { count, hasSeries: seriesPixels > 0, profileCount, integrates, row: 0, drained: [], rows: 0 };
	if (probes.hasSeries) {
		probes.pixels = storage(pixels.length, pixels.slice());
		probes.info = storage(count * 16, info.slice());
		probes.history = storage(historyRows * count * 4);
		probes.params = uniform(16);
		const made = pipeline('probe', probeShader);
		probes.groups = [0, 1].map(newest => group(made, [probes.params, e.field[newest], e.inten1, e.inten, probes.pixels, probes.info, probes.history]));
	}
	if (profileCount > 0) {
		probes.profile = storage(profileCount * 16, profilePixels.slice());
		probes.total = storage(profileCount * 4);
		probes.out = storage(profileCount * 4);
		probes.profileParams = uniform(16);
		const gather = pipeline('gather', () => profileShader(true));
		probes.gatherGroups = [0, 1].map(newest => group(gather, [probes.profileParams, e.field[newest], e.inten1, e.inten, probes.profile, probes.total, probes.out]));
		if (integrates) {
			const accumulate = pipeline('accumulate', () => profileShader(false));
			probes.accumulateGroup = group(accumulate, [probes.profileParams, null, e.inten1, e.inten, probes.profile, probes.total]);
		}
	}
	e.probes = probes;
}

let historyRows = 2048;
export function setHistoryRows(rows) { historyRows = rows; }

async function sample(e) {
	const probes = e.probes;
	if (!probes) { return; }
	if (probes.hasSeries) {
		if (probes.row >= historyRows) { await drain(e); }
		const data = new ArrayBuffer(16);
		new Int32Array(data, 0, 2).set([probes.row, probes.count]);
		new Float32Array(data, 8, 1)[0] = e.intensityScale;
		device.queue.writeBuffer(probes.params, 0, data);
		const encoder = device.createCommandEncoder();
		pass(encoder, pipeline('probe'), probes.groups[e.current], Math.ceil(probes.count / 64));
		device.queue.submit([encoder.finish()]);
		probes.row++;
	}
	if (probes.integrates) {
		writeProfileParams(e, Math.round(e.time));
		const encoder = device.createCommandEncoder();
		pass(encoder, pipeline('accumulate'), probes.accumulateGroup, Math.ceil(probes.profileCount / 64));
		device.queue.submit([encoder.finish()]);
	}
}

function writeProfileParams(e, stepNumber) {
	const data = new ArrayBuffer(16);
	new Int32Array(data, 0, 2).set([stepNumber, e.probes.profileCount]);
	new Float32Array(data, 8, 1)[0] = e.intensityScale;
	device.queue.writeBuffer(e.probes.profileParams, 0, data);
}

// the history's rows sampled so far, read from the card and kept here until the program takes them
async function drain(e) {
	const probes = e.probes;
	if (!probes || probes.row === 0) { return; }
	const bytes = probes.row * probes.count * 4;
	const target = readBuffer(e, bytes);
	const encoder = device.createCommandEncoder();
	encoder.copyBufferToBuffer(probes.history, 0, target, 0, bytes);
	device.queue.submit([encoder.finish()]);
	await target.mapAsync(GPUMapMode.READ, 0, bytes);
	probes.drained.push(new Float32Array(target.getMappedRange(0, bytes).slice(0)));
	release(e, target);
	probes.rows += probes.row;
	probes.row = 0;
}

// how many rows of point-probe values wait to be taken, the card's pending ones read first
export async function probeRows(number) {
	const e = engines.get(number);
	if (!e.probes || !e.probes.hasSeries) { return 0; }
	await drain(e);
	return e.probes.rows;
}

// those rows, row after row, count floats each, into the program's memory; then they are gone here
export function takeProbeRows(number, address) {
	const probes = engines.get(number).probes;
	let offset = address >>> 0;
	for (const rows of probes.drained) {
		heap().set(new Uint8Array(rows.buffer, rows.byteOffset, rows.byteLength), offset);
		offset += rows.byteLength;
	}
	probes.drained = [];
	probes.rows = 0;
}

// the line probes' values in the current state, flat, into the program's memory
export async function readProfiles(number, address) {
	const e = engines.get(number);
	const probes = e.probes;
	writeProfileParams(e, Math.round(e.time));
	const encoder = device.createCommandEncoder();
	pass(encoder, pipeline('gather'), probes.gatherGroups[e.current], Math.ceil(probes.profileCount / 64));
	device.queue.submit([encoder.finish()]);
	await download(e, probes.out, probes.profileCount * 4, address);
}

// ---- the end

export function destroy(number) {
	const e = engines.get(number);
	if (!e) { return; }
	engines.delete(number);
	setProbesOf(e);
	const buffers = [...e.field, e.inten1, e.inten, e.c2Index, e.dIndex, e.c2Table, e.dTable, e.frequencies, e.imageSources, e.objectSources,
		...e.stepParams, e.edgeParams, e.imageParams, ...e.objectPasses.map(objects => objects.params), ...e.staging, ...e.captureParams];
	for (const pool of e.readPool.values()) { buffers.push(...pool); }
	for (const taken of e.captures.values()) { buffers.push(...taken.targets); }
	for (const buffer of buffers) { if (buffer) { buffer.destroy(); } }
}

function setProbesOf(e) {
	if (!e.probes) { return; }
	for (const buffer of [e.probes.pixels, e.probes.info, e.probes.history, e.probes.profile, e.probes.total, e.probes.out, e.probes.params, e.probes.profileParams]) { if (buffer) { buffer.destroy(); } }
	e.probes = null;
}
