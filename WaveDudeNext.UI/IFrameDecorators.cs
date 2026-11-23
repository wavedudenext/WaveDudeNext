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

using WaveDudeNext.Model;
using WaveDudeNext.Probes;
using WaveDudeNext.Runs;

namespace WaveDudeNext.UI;

/// <summary>
/// What the application draws over an export's frames beyond the colours and the outline: the probe graph boxes and
/// markers and the source markers, text and shapes a UI framework rasterises (2026-11-07; each application implements
/// it). Which of them the project's display settings want is the implementation's to read. Made on the UI thread once
/// per export; its painters run on the export's own thread, one frame at a time, in rising order.
/// </summary>
public interface IFrameDecorators
{
	/// <summary>
	/// The decorator of an export of the run <paramref name="spec"/> describes, for the display settings of
	/// <paramref name="project"/> as they are now.
	/// </summary>
	/// <param name="project">The project: its display settings select the decorations, its probes and sources are drawn.</param>
	/// <param name="spec">The run exported: the frame size, the steps, the frame interval, the probes.</param>
	/// <param name="traces">
	/// The probes' recordings, in the spec's probe order: a preview run's, complete, or an export run's
	/// (<see cref="ExportRun.Traces"/>), which hold each recording up to the frame being painted and no further, and
	/// are read on the export's thread alone; a graph box shows the recording up to its frame either way.
	/// </param>
	IFrameDecorator Create(Project project, RunSpec spec, IReadOnlyList<ProbeTrace> traces);
}

/// <summary>One export's decorations (<see cref="IFrameDecorators"/>).</summary>
public interface IFrameDecorator
{
	/// <summary>The painter of <paramref name="layer"/>'s frames, called with the frame's index and its pixels (BGRA as ints, the canvas's size) after the colours and the outline; null when nothing is drawn on that layer.</summary>
	Action<int, Memory<int>>? For(FrameLayer layer);
}
