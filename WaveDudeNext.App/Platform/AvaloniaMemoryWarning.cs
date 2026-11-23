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
//   Mark <mark@pab-ai.com>, co-author of the AvaloniaUI port
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

using Avalonia.Controls;
using WaveDudeNext.App.Dialogs;
using WaveDudeNext.Runs;
using WaveDudeNext.UI;

namespace WaveDudeNext.App.Platform;

/// <summary>
/// The memory warning for the models of <c>WaveDudeNext.UI</c> (<see cref="IMemoryWarning"/>): the
/// <see cref="MemoryWarningView"/>, modal, in a window over the one <paramref name="anchor"/> is in, or over the main
/// view where there are no windows (the browser head). Avalonia's dialogs answer later, so the task completes when the
/// dialog closes and the models go on from the UI thread then (the dialog step of 2026-11-09 made the interface answer
/// as a task for this).
/// </summary>
/// <param name="anchor">A control of the view the dialog belongs to; where it is shown is looked up when the question is asked.</param>
public sealed class AvaloniaMemoryWarning(Control anchor) : IMemoryWarning
{
	/// <inheritdoc/>
	public Task<MemoryWarningChoice?> AskAsync(RunSpec spec, long budget, long autoBudget, PreviewQuality current) =>
		MemoryWarningView.Show(anchor, spec, budget, autoBudget, current);
}
