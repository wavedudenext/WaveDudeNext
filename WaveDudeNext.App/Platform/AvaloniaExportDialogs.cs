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
using Avalonia.Platform.Storage;
using WaveDudeNext.App.Dialogs;
using WaveDudeNext.Export;
using WaveDudeNext.UI;

namespace WaveDudeNext.App.Platform;

/// <summary>
/// The export dialogs for the models of <c>WaveDudeNext.UI</c> (<see cref="IExportDialogs"/>): the platform's Save dialog
/// through Avalonia's storage provider with the title and the filters of the string table, its filter order being
/// <see cref="ExportFormats.All"/>'s, <see cref="ExportFormats.Stills"/>' for a still and one CSV filter for a probe's
/// data; and the application's message window for the question about a sequence folder. Both answer later, as
/// Avalonia's dialogs do; the models go on from the UI thread when they have. The top level is looked up when a
/// dialog is asked for: the storage provider is any top level's, the message window needs a window, and where there
/// is none (the browser head, whose dialogs are still to come) the folder question answers No.
/// </summary>
/// <param name="anchor">A control of the view the dialogs belong to, whose resources hold the strings.</param>
public sealed class AvaloniaExportDialogs(Control anchor) : IExportDialogs
{
	/// <inheritdoc/>
	public async Task<SavePick?> AskWhereToSaveAsync(ExportSubject subject, string folder, string name, IReadOnlyList<ExportFormat> formats, int formatIndex)
	{
		IReadOnlyList<FilePickerFileType> types = FileTypesFrom(Text(subject switch
		{
			ExportSubject.Still => "ExportDialogFilterStill",
			ExportSubject.ProbeData => "ExportDialogFilterCsv",
			_ => "ExportDialogFilter",
		}));
		int preset = Math.Clamp(formatIndex, 0, Math.Max(0, types.Count - 1));
		if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { } storage)
		{
			return null;
		}
		var options = new FilePickerSaveOptions
		{
			Title = Text(subject switch
			{
				ExportSubject.Field => "ExportDialogTitleField",
				ExportSubject.Intensity => "ExportDialogTitleIntensity",
				ExportSubject.Both => "ExportDialogTitleBoth",
				ExportSubject.Still => "ExportDialogTitleFrame",
				_ => "ExportDialogTitleProbeCsv",
			}),
			SuggestedFileName = name,
			DefaultExtension = (formats.Count > 0 ? ExportFormats.Extension(formats[Math.Clamp(formatIndex, 0, formats.Count - 1)]) : ProbeCsv.Extension).TrimStart('.'),
			FileTypeChoices = types,
			SuggestedFileType = types.Count > 0 ? types[preset] : null,
			ShowOverwritePrompt = true,
			SuggestedStartLocation = folder.Length > 0 && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null,
		};
		SaveFilePickerResult picked = await storage.SaveFilePickerWithResultAsync(options);
		string? path = picked.File?.TryGetLocalPath();
		if (path == null)
		{
			return null;
		}
		// the filter the user chose: macOS says which one. Where the platform does not (SelectedFileType is null), it is read
		// from the extension typed or appended, which cannot tell apart filters that share one (the QuickTime movies are all
		// .mov: the first of them, the Animation codec, would be taken whatever was chosen); a path that names none of them
		// keeps the one offered first
		int reported = picked.SelectedFileType is { } selected ? IndexOfFileType(types, selected) : -1;
		int chosen = reported >= 0
			? reported
			: formats.Count > 0 && ExportFormats.FromPath(path) is { } format ? Math.Max(0, formats.ToList().IndexOf(format)) : preset;
		return new SavePick(path, chosen);
	}

	/// <summary>The place of a file type in the list that was offered: the same object, which is what the platform hands back, else the same label.</summary>
	private static int IndexOfFileType(IReadOnlyList<FilePickerFileType> types, FilePickerFileType type)
	{
		for (int i = 0; i < types.Count; i++)
		{
			if (ReferenceEquals(types[i], type) || types[i].Name == type.Name)
			{
				return i;
			}
		}
		return -1;
	}

	/// <inheritdoc/>
	public async Task<bool> AskToReplaceFolderAsync(string folder) =>
		TopLevel.GetTopLevel(anchor) is Window owner
		&& await MessageWindow.Show(owner, string.Format(Text("ExportFolderExistsFormat"), folder), MessageKind.Question, MessageButtons.YesNo) == MessageAnswer.Yes;

	/// <summary>"Label|*.ext|Label 2|*.a;*.b" (the Win32 filter syntax the string tables keep) into picker file types.</summary>
	internal static IReadOnlyList<FilePickerFileType> FileTypesFrom(string filter)
	{
		string[] parts = filter.Split('|');
		var types = new List<FilePickerFileType>();
		for (int i = 0; i + 1 < parts.Length; i += 2)
		{
			types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
		}
		return types;
	}

	private string Text(string key) => anchor.FindResource(key) as string ?? key;
}
