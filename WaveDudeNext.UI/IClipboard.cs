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

namespace WaveDudeNext.UI;

/// <summary>
/// The system's clipboard as the models of this library need it (2026-11-06; each application implements it, WPF over
/// its <c>Clipboard</c>): text put there under a format of the application's own and as plain text, and read back.
/// Called on the UI thread, and answered at once: the commands ask whether there is something to paste every time a
/// menu shows, so an application whose clipboard only answers later keeps what it last read and answers with that.
/// </summary>
public interface IClipboard
{
	/// <summary>
	/// A number that changes whenever the clipboard's content does (Windows counts its changes), so what was read is
	/// kept until then; 0 when it cannot be told, and the clipboard is then read every time.
	/// </summary>
	uint ChangeCount { get; }

	/// <summary>
	/// Puts <paramref name="text"/> on the clipboard twice, under <paramref name="format"/> and as plain text, so a
	/// text editor takes it too; it stays there when the application ends.
	/// </summary>
	/// <returns>
	/// False when the clipboard refused (another program holds it open); the implementation logs that. An application
	/// whose clipboard only answers later answers true here and logs a refusal when it comes; what must not go on
	/// after a refusal asks <see cref="SetTextAsync"/>.
	/// </returns>
	bool TrySetText(string format, string text);

	/// <summary>
	/// <see cref="TrySetText"/>, answered when the clipboard has taken the text or refused it (2026-11-22, for Cut,
	/// which must not remove what the clipboard did not take): at once where the clipboard answers at once, which is
	/// this default; later where it answers later (a browser may refuse a page the clipboard, and says so only then).
	/// </summary>
	/// <returns>Whether the clipboard holds the text now; the implementation logs a refusal.</returns>
	Task<bool> SetTextAsync(string format, string text) => Task.FromResult(TrySetText(format, text));

	/// <summary>
	/// Reads the clipboard once: the text it holds under <paramref name="format"/>, else its plain text, else null.
	/// </summary>
	/// <param name="format">The application's own format, looked for first.</param>
	/// <param name="text">The text found, or null when the clipboard holds neither.</param>
	/// <returns>False when the clipboard cannot be read now (another program holds it open): nothing is known, and the next question asks again.</returns>
	bool TryGetText(string format, out string? text);
}
