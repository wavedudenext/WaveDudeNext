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

using System.Collections.Concurrent;
using WaveDudeNext.Model;
using WaveDudeNext.Probes;

namespace WaveDudeNext.Engine;

/// <summary>
/// The host side of a GPU engine: what a scene, the options, the emitters and the probes come to as the tables a card
/// takes, one source for every engine that runs the kernels on a device (2026-11-13, the user's choice when the
/// browser's WebGPU engine came beside the OpenCL one: the numerics change, and two copies of this would drift). The
/// kernels differ by their language (OpenCL C, WGSL); these arrays do not: the padded layout of the CPU engine (a
/// border of one cell, <c>stride = width + 2</c>, the cell of (x, y) at <c>(y + 1) * stride + x + 1</c>), the compact
/// scene maps, the source and emitter tables, the probes' pixel lists. All pure functions of their arguments; any
/// thread. Units are the engines': dx = dt = 1, frequencies in radians per step, time in steps.
/// </summary>
public static class GpuTables
{
	/// <summary>The least room of the damping table's buffer: a scene swapped in later (<see cref="IWaveEngine.TryReplaceScene"/>) fits unless it has more distinct damping values.</summary>
	public const int DTableRoom = 4096;

	/// <summary>Rows of the point probes' history on the device: after that many sampled steps it is read to the host.</summary>
	public const int ProbeRows = 2048;

	/// <summary>The speed factor of the open edges' absorbing condition, the CPU engine's.</summary>
	public const float EdgeSpeedFactor = WaveSimulator.EdgeSpeedFactor;

	/// <summary>The scene's maps as a card takes them: per-cell indices, the tables they address, the sub-steps and the c² multiplier (<see cref="Scene"/>).</summary>
	/// <param name="C2Index">Per cell, padded: the index of its c² in <paramref name="C2Table"/>.</param>
	/// <param name="C2Table">The scene's distinct c² values, sorted; at most 256.</param>
	/// <param name="DIndex">Per cell, padded: the index of its damping in <paramref name="DTable"/>.</param>
	/// <param name="DTable">The scene's distinct damping values, sorted, already taken to their 1 / sub-steps-th root.</param>
	/// <param name="SubSteps">Kernel launches a step: 1, or what the 4th-order stencil needs for this scene's highest c².</param>
	/// <param name="C2Mult">1 / sub-steps², the kernel's c² scale; exactly 1 at one sub-step.</param>
	public sealed record SceneTables(byte[] C2Index, float[] C2Table, ushort[] DIndex, float[] DTable, int SubSteps, float C2Mult);

	/// <summary>
	/// The scene's per-cell maps for a card: the step kernel is bound by the card's memory bandwidth, so a cell carries a byte
	/// index into the table of the scene's distinct c² values (at most 256: they come from the image's red levels) and a
	/// 16-bit index into the table of its distinct damping values (a few hundred: the blue levels and the rim's depths),
	/// 3 bytes instead of 8 (2026-11-03, the user's choice); the tables hold the very floats the CPU engine computes
	/// with, so the arithmetic is unchanged. In the padded layout (a border of one cell, like the CPU engine), with the
	/// sub-steps and their corrections: sub-stepping keeps the 4th-order stencil stable without changing the physical
	/// wave speed or damping, and stays at 1 unless the stencil is in use and this scene's highest c² needs it. The
	/// damping table is pre-scaled to its 1 / sub-steps-th root (unchanged at 1 sub-step), as the CPU engine's cells are,
	/// and the c² multiplier is 1 / sub-steps², exactly 1f at one sub-step (a bit-for-bit no-op multiply in the kernel).
	/// </summary>
	/// <param name="scene">Per-cell c² and damping of the grid, unpadded.</param>
	/// <param name="fourthOrder">Whether the 4th-order stencil is in use (<see cref="EngineOptions.FourthOrderLaplacian"/>).</param>
	/// <exception cref="InvalidOperationException">The scene has more than 256 distinct c² values.</exception>
	public static SceneTables Scene(SceneData scene, bool fourthOrder)
	{
		int width = scene.Width;
		int height = scene.Height;
		int stride = width + 2;
		int cells = stride * (height + 2);
		float[] c2 = new float[cells];
		float[] damping = new float[cells];
		for (int y = 0; y < height; y++)
		{
			Array.Copy(scene.C2, y * width, c2, (y + 1) * stride + 1, width);
			Array.Copy(scene.D, y * width, damping, (y + 1) * stride + 1, width);
		}
		float[] c2Table = Distinct(c2);
		float[] dTable = Distinct(damping);
		if (c2Table.Length > 256)
		{
			throw new InvalidOperationException("the scene has more than 256 distinct refractive indices, which a byte index cannot address");
		}
		byte[] c2Index = new byte[cells];
		ushort[] dIndex = new ushort[cells];
		Parallel.For(0, height + 2, row =>
		{
			int start = row * stride;
			// a scene's values come in runs, and a cell equal to the one before it has that cell's index: the search is
			// made where the value changes (see Distinct; a NaN equals nothing and is searched every time, as before)
			float lastC2 = float.NaN;
			float lastDamping = float.NaN;
			byte c2At = 0;
			ushort dampingAt = 0;
			for (int i = start; i < start + stride; i++)
			{
				if (c2[i] != lastC2)
				{
					lastC2 = c2[i];
					c2At = (byte)Array.BinarySearch(c2Table, lastC2);
				}
				c2Index[i] = c2At;
				if (damping[i] != lastDamping)
				{
					lastDamping = damping[i];
					dampingAt = (ushort)Array.BinarySearch(dTable, lastDamping);
				}
				dIndex[i] = dampingAt;
			}
		});
		int subSteps = fourthOrder ? WaveSimulator.ComputeSubSteps(WaveSimulator.MaxOf(c2Table)) : 1;
		float c2Mult = 1f;
		if (subSteps > 1)
		{
			// Damping is applied once per sub-step, so take the sub-step root to keep the per-step decay unchanged.
			double exponent = 1.0 / subSteps;
			for (int i = 0; i < dTable.Length; i++)
			{
				dTable[i] = (float)Math.Pow(dTable[i], exponent);
			}
			// A sub-step is 1/subSteps long, so c²·dt² shrinks by 1/subSteps².
			c2Mult = 1f / (subSteps * subSteps);
		}
		return new SceneTables(c2Index, c2Table, dIndex, dTable, subSteps, c2Mult);
	}

	/// <summary>
	/// The distinct values of <paramref name="values"/>, sorted, so <see cref="Array.BinarySearch(Array, object)"/> finds a
	/// value's index; gathered on every core. A value equal to the one before it is not looked up in the set (2026-11-13):
	/// a scene's values come in runs, an index or a damping holding for many cells of a row, and in the browser, where
	/// the set's code runs many times slower than on a desktop, a look at every cell of a full-HD grid made an engine
	/// take 2 s to build. The result is the same set.
	/// </summary>
	private static float[] Distinct(float[] values)
	{
		var all = new HashSet<float>();
		var gate = new Lock();
		Parallel.ForEach(Partitioner.Create(0, values.Length, 1 << 16), () => new HashSet<float>(), (range, _, local) =>
		{
			// NaN equals nothing, so the first value is always added, and a NaN among the values every time, as before
			float last = float.NaN;
			for (int i = range.Item1; i < range.Item2; i++)
			{
				float value = values[i];
				if (value != last)
				{
					local.Add(value);
					last = value;
				}
			}
			return local;
		}, local =>
		{
			lock (gate)
			{
				all.UnionWith(local);
			}
		});
		float[] table = [.. all];
		Array.Sort(table);
		return table;
	}

	/// <summary>The level-to-frequency table of the image sources: level 0 to 255 to radians per step, <c>level / 255 × 0.5 × scale</c>.</summary>
	/// <param name="sourceFrequencyScale"><see cref="EngineOptions.SourceFrequencyScale"/>.</param>
	public static float[] Frequencies(double sourceFrequencyScale)
	{
		float[] frequencies = new float[256];
		for (int level = 0; level <= 255; level++)
		{
			frequencies[level] = (float)(level / 255.0 * 0.5 * sourceFrequencyScale);
		}
		return frequencies;
	}

	/// <summary>The image sources (the green channel of the scene image) as the kernel's tables, padded cell index and level per source, noting the levels in use in <paramref name="levelUsed"/> afresh.</summary>
	/// <param name="scene">The scene with its image sources.</param>
	/// <param name="levelUsed">256 flags, cleared and then set for every level a source has.</param>
	public static (int[] Indices, int[] Levels) ImageSources(SceneData scene, bool[] levelUsed)
	{
		int count = scene.SourceCount;
		int stride = scene.Width + 2;
		int[] indices = new int[count];
		int[] levels = new int[count];
		Array.Clear(levelUsed);
		for (int i = 0; i < count; i++)
		{
			indices[i] = (scene.SourceY[i] + 1) * stride + scene.SourceX[i] + 1;
			levels[i] = scene.SourceLevel[i];
			levelUsed[scene.SourceLevel[i]] = true;
		}
		return (indices, levels);
	}

	/// <summary>The emitters inside the canvas: a randomized composite element can fall outside, and both kinds of engine drop those.</summary>
	/// <param name="emitters">The emitters of the object sources, or null for none.</param>
	/// <param name="canvasWidth">Canvas width in cells, without the rim.</param>
	/// <param name="canvasHeight">Canvas height in cells, without the rim.</param>
	public static List<Emitter> InCanvas(IReadOnlyList<Emitter>? emitters, int canvasWidth, int canvasHeight)
	{
		var inside = new List<Emitter>();
		if (emitters != null)
		{
			foreach (Emitter source in emitters)
			{
				if (source.X >= 0 && source.X < canvasWidth && source.Y >= 0 && source.Y < canvasHeight)
				{
					inside.Add(source);
				}
			}
		}
		return inside;
	}

	/// <summary>
	/// The emitters in the order a card applies them, in passes: within a pass no two emitters drive the same cell, and an
	/// emitter that shares its cell with earlier ones comes in a later pass than each of them. A card applies a pass's
	/// emitters at once, each reading its cell and writing it back (the soft source's blend), so two on one cell in the
	/// same launch race: one's drive is lost, which one changing from step to step and run to run. The passes, launched
	/// one after the other, give every cell its emitters in the list's order, which is the CPU engine's. Almost every
	/// project has one pass, the list as it is; a composite source with random positions, or two sources put on one
	/// pixel, has a second (2026-11-19, found by the release tests: Example 6's 225 emitters fall on 224 cells, and its
	/// field on the card differed between two runs by up to a third of its largest value, in every version so far).
	/// </summary>
	/// <param name="sources">The emitters inside the canvas, in the project's order (<see cref="InCanvas"/>).</param>
	/// <returns>The emitters by pass, each pass in the list's order, and for every pass the count of emitters up to its end.</returns>
	public static (List<Emitter> Ordered, int[] PassEnds) Passes(IReadOnlyList<Emitter> sources)
	{
		// the pass of an emitter: how many emitters before it in the list share its cell
		var seen = new Dictionary<(int X, int Y), int>();
		var passes = new List<List<Emitter>>();
		foreach (Emitter source in sources)
		{
			seen.TryGetValue((source.X, source.Y), out int pass);
			seen[(source.X, source.Y)] = pass + 1;
			if (pass == passes.Count)
			{
				passes.Add([]);
			}
			passes[pass].Add(source);
		}
		var ordered = new List<Emitter>(sources.Count);
		int[] ends = new int[passes.Count];
		for (int pass = 0; pass < passes.Count; pass++)
		{
			ordered.AddRange(passes[pass]);
			ends[pass] = ordered.Count;
		}
		return (ordered, ends);
	}

	/// <summary>
	/// The object sources as the kernel's tables, one entry per emitter (see the <c>apply_object_sources</c> kernel for what
	/// each drives). <paramref name="OffWidth"/>, <paramref name="CycleAnchor"/> and <paramref name="RngState"/> are the
	/// kernel's own state from then on: an element that re-randomizes its off-width redraws it on the card.
	/// </summary>
	/// <param name="Index">Padded cell index, the rim included.</param>
	/// <param name="Omega">Angular frequency, radians per step, by the image sources' level formula from the decimal level.</param>
	/// <param name="Amplitude">The element's amplitude.</param>
	/// <param name="Phase">Phase in radians.</param>
	/// <param name="Mode">0 continuous, 1 pulsed.</param>
	/// <param name="PulseShape">0 Gaussian, 1 smoothed square.</param>
	/// <param name="OnWidth">On-width in cycles.</param>
	/// <param name="OffWidth">The off-width in effect, in cycles.</param>
	/// <param name="DetachStep">The step after which the element fades out, or -1.</param>
	/// <param name="OffWidthBase">The centre of a randomized off-width.</param>
	/// <param name="RandomizeOffWidthAmount">Its spread; 0 or less for a fixed off-width.</param>
	/// <param name="RngState">The element's xorshift32 seed, never 0; only the bits matter.</param>
	/// <param name="CycleAnchor">Where the element's current cycle started, in cycles; 0 at the start.</param>
	public sealed record EmitterTables(int[] Index, float[] Omega, float[] Amplitude, float[] Phase, int[] Mode, int[] PulseShape, int[] OnWidth, int[] OffWidth,
		int[] DetachStep, int[] OffWidthBase, int[] RandomizeOffWidthAmount, int[] RngState, float[] CycleAnchor);

	/// <summary>The tables of <paramref name="sources"/>, which lie inside the canvas (<see cref="InCanvas"/>).</summary>
	/// <param name="sources">The emitters to drive.</param>
	/// <param name="rim">Cells of rim around the canvas (<see cref="SceneData.Rim"/>).</param>
	/// <param name="stride">Floats per row of the padded grid.</param>
	/// <param name="sourceFrequencyScale"><see cref="EngineOptions.SourceFrequencyScale"/>.</param>
	public static EmitterTables Emitters(IReadOnlyList<Emitter> sources, int rim, int stride, double sourceFrequencyScale)
	{
		int n = sources.Count;
		int[] index = new int[n], mode = new int[n], pulseShape = new int[n];
		int[] onWidth = new int[n], offWidth = new int[n], detachStep = new int[n];
		int[] offWidthBase = new int[n], randomizeOffWidthAmount = new int[n], rngState = new int[n];
		float[] omega = new float[n], amplitude = new float[n], phase = new float[n], cycleAnchor = new float[n];
		for (int i = 0; i < n; i++)
		{
			Emitter source = sources[i];
			index[i] = (source.Y + 1 + rim) * stride + source.X + 1 + rim;
			omega[i] = (float)(source.Level / 255.0 * 0.5 * sourceFrequencyScale);
			amplitude[i] = (float)source.Amplitude;
			phase[i] = (float)source.PhaseRadians;
			mode[i] = source.Mode == SourceMode.Pulsed ? 1 : 0;
			pulseShape[i] = source.PulseShape == PulseShape.SmoothedSquare ? 1 : 0;
			onWidth[i] = source.OnWidthCycles;
			offWidth[i] = source.OffWidthCycles;
			detachStep[i] = source.DetachAfterStep;
			offWidthBase[i] = source.OffWidthBase;
			randomizeOffWidthAmount[i] = source.RandomizeOffWidthAmount;
			// the per-element seed drawn from the project seed (never 0, see Emitter.FromSource); the kernel
			// reinterprets it as uint, only the bits matter
			rngState[i] = (int)source.RngSeed;
		}
		return new EmitterTables(index, omega, amplitude, phase, mode, pulseShape, onWidth, offWidth, detachStep, offWidthBase, randomizeOffWidthAmount, rngState, cycleAnchor);
	}

	/// <summary>
	/// The intensity's retention per step: its averaging time constant scales inversely with the frequency of the slowest
	/// active source, image or object (see <see cref="WaveSimulator"/>'s identical computation and <see cref="Simulation.TimeDecay"/>).
	/// </summary>
	/// <param name="timeDecay"><see cref="EngineOptions.TimeDecay"/>.</param>
	/// <param name="levelUsed">The image sources' levels in use (<see cref="ImageSources"/>).</param>
	/// <param name="sources">The object sources in the canvas.</param>
	public static float IntensityAlpha(double timeDecay, bool[] levelUsed, IReadOnlyList<Emitter> sources) =>
		WaveSimulator.IntensityRetention(timeDecay, WaveSimulator.LowestSourceLevel(levelUsed, sources));

	/// <summary>The stencil for the log, every engine alike: "9-point stencil", or "4th-order stencil, 3 sub-steps".</summary>
	public static string StencilText(bool fourthOrder, int subSteps) => WaveSimulator.StencilText(fourthOrder, subSteps);

	/// <summary>The point probes for the <c>probe_sample</c> kernel.</summary>
	/// <param name="Pixels">The padded cell indices of all point probes, concatenated; one dummy entry when there are none.</param>
	/// <param name="Info">Four ints per probe of the sampler: start in <paramref name="Pixels"/>, count, 1 = sample the intensity, 1 = divide by the count; all 0 for a line probe.</param>
	public sealed record SeriesProbeTables(int[] Pixels, int[] Info);

	/// <summary>
	/// Packs the pixel lists of all point (non-profile) probes and describes each probe in an info table (point probes
	/// average their pixels, so a single-pixel probe yields that pixel's value like <see cref="ProbeSampler.Compute"/>).
	/// </summary>
	public static SeriesProbeTables SeriesProbes(ProbeSampler sampler)
	{
		int count = sampler.Count;
		int totalPixels = 0;
		for (int i = 0; i < count; i++)
		{
			if (!sampler.IsProfile[i])
			{
				totalPixels += sampler.Pixels[i].Length;
			}
		}
		int[] pixels = new int[Math.Max(totalPixels, 1)];
		int[] info = new int[count * 4];
		int offset = 0;
		for (int i = 0; i < count; i++)
		{
			if (!sampler.IsProfile[i])
			{
				int[] probePixels = sampler.Pixels[i];
				Array.Copy(probePixels, 0, pixels, offset, probePixels.Length);
				info[i * 4] = offset;
				info[i * 4 + 1] = probePixels.Length;
				info[i * 4 + 2] = sampler.UseIntensity[i] ? 1 : 0;
				info[i * 4 + 3] = 1;
				offset += probePixels.Length;
			}
		}
		return new SeriesProbeTables(pixels, info);
	}

	/// <summary>The line probes for the <c>profile_accumulate</c> and <c>profile_gather</c> kernels, every line's pixels flattened into one list.</summary>
	/// <param name="Pixels">Padded cell index per line pixel.</param>
	/// <param name="Modes">Per pixel: 0 the field, 1 the intensity, 2 the intensity's total over the steps.</param>
	/// <param name="StartFlags">Per pixel: 1 when its probe starts recording at a step.</param>
	/// <param name="StartSteps">Per pixel: that step.</param>
	/// <param name="ProfileStart">Per probe of the sampler: where its pixels start in the flat lists.</param>
	/// <param name="Integrates">Whether any line integrates (mode 2), which needs the accumulation every step.</param>
	public sealed record ProfileProbeTables(int[] Pixels, int[] Modes, int[] StartFlags, int[] StartSteps, int[] ProfileStart, bool Integrates);

	/// <summary>Flattens the pixels of all line probes into per-pixel lists (index, mode, start flag and step) and records where each probe starts.</summary>
	public static ProfileProbeTables ProfileProbes(ProbeSampler sampler)
	{
		int count = sampler.Count;
		int totalPixels = 0;
		for (int i = 0; i < count; i++)
		{
			if (sampler.IsProfile[i])
			{
				totalPixels += sampler.Pixels[i].Length;
			}
		}
		int[] pixels = new int[totalPixels];
		int[] modes = new int[totalPixels];
		int[] startFlags = new int[totalPixels];
		int[] startSteps = new int[totalPixels];
		int[] profileStart = new int[count];
		bool integrates = false;
		int offset = 0;
		for (int i = 0; i < count; i++)
		{
			profileStart[i] = offset;
			if (!sampler.IsProfile[i])
			{
				continue;
			}
			int[] probePixels = sampler.Pixels[i];
			int mode = sampler.Integrate[i] ? 2 : (sampler.UseIntensity[i] ? 1 : 0);
			if (mode == 2)
			{
				integrates = true;
			}
			int startFlag = sampler.UseStartAfterStep[i] ? 1 : 0;
			int startStep = sampler.StartAfterStep[i];
			for (int k = 0; k < probePixels.Length; k++)
			{
				pixels[offset + k] = probePixels[k];
				modes[offset + k] = mode;
				startFlags[offset + k] = startFlag;
				startSteps[offset + k] = startStep;
			}
			offset += probePixels.Length;
		}
		return new ProfileProbeTables(pixels, modes, startFlags, startSteps, profileStart, integrates);
	}
}
