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

using System.Text.RegularExpressions;
using Avalonia.Platform.Storage;
using WaveDudeNext.Rendering;
using WaveDudeNext.UI;

namespace WaveDudeNext.App.Platform;

/// <summary>
/// The files of the platform's pickers as this head uses them (2026-11-15, for the browser head): the file types a
/// picker lists, and reading and writing a picked file that has no path. On a desktop a picked file is a path and
/// everything here passes it on. A browser hands a page a file's name and a stream that answers only asynchronously,
/// so there a file is read into memory or into a copy in the runtime's own temporary folder, for the readers that
/// take a path, and written from memory or from a file the program wrote there. UI thread.
/// </summary>
internal static partial class PickedFiles
{
	private static readonly NLog.Logger Logger = NLog.LogManager.GetLogger(typeof(PickedFiles).FullName!);

	/// <summary>
	/// The platform's dialog for a file to open: what was picked; empty when nothing was, and when the platform would
	/// not show the dialog (<see cref="Refused"/>).
	/// </summary>
	public static async Task<IReadOnlyList<IStorageFile>> OpenAsync(IStorageProvider storage, FilePickerOpenOptions options)
	{
		try
		{
			return await storage.OpenFilePickerAsync(options);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Refused(ex);
			return [];
		}
	}

	/// <summary>The platform's dialog for a file to write: the file; null when none was picked, and when the platform would not show the dialog (<see cref="Refused"/>).</summary>
	public static async Task<IStorageFile?> SaveAsync(IStorageProvider storage, FilePickerSaveOptions options)
	{
		try
		{
			return await storage.SaveFilePickerAsync(options);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Refused(ex);
			return null;
		}
	}

	/// <summary>The platform's dialog for a folder: what was picked; empty when nothing was, and when the platform would not show the dialog (<see cref="Refused"/>).</summary>
	public static async Task<IReadOnlyList<IStorageFolder>> FolderAsync(IStorageProvider storage, FolderPickerOpenOptions options)
	{
		try
		{
			return await storage.OpenFolderPickerAsync(options);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Refused(ex);
			return [];
		}
	}

	/// <summary>
	/// A dialog the platform would not show is no error of the program's, and counts as one closed without a choice,
	/// with a line that says so (a warning, so the status bar shows it). A browser shows a file dialog only right
	/// after a click or a key press, for about five seconds, and refuses one asked for later with an error of its
	/// own; a command that reaches the program late, or asks its question after something else took its time, meets
	/// that. Until 2026-11-20 nothing caught the refusal and it ended the program (found by a key sequence in the
	/// browser head: Export intensity pressed while the manual was still being laid out, the dialog asked for once
	/// the manual had closed, "Must be handling a user gesture to show a file picker", and the page's notice that
	/// the program has stopped).
	/// </summary>
	private static void Refused(Exception error) =>
		Logger.Warn(OperatingSystem.IsBrowser()
			? "The file dialog was not shown: the browser shows one only right after a click or a key press. Choose the command again. ({Reason})"
			: "The file dialog was not shown. ({Reason})", error.Message);

	/// <summary>
	/// "Label (*.a;*.b)|*.a;*.b|Label 2|*.c" (the Win32 filter syntax the string tables keep) as the picker's file
	/// types, a name and its patterns per pair. In a browser each type also names a media type and its name loses
	/// the pattern in brackets: Avalonia's browser picker hands the browser only the types that name a media type
	/// (its <c>BrowserStorageProvider.ConvertFileTypes</c>, read at 12.1.5; the extensions come from the patterns)
	/// and the browser's own "All files" when none is left, which is all the dialogs showed there (the user's report);
	/// and Chromium takes every asterisk out of a type's description before Windows appends the pattern itself
	/// (<c>FormatFilterForExtensions</c> with <c>keep_extension_visible</c>, which the file pickers of a page set), so
	/// "Projects (*.wdproj)" came out as "Projects (.wdproj) (*.wdproj)". An entry for every file is the picker's own
	/// there.
	/// </summary>
	public static IReadOnlyList<FilePickerFileType> TypesFrom(string filter)
	{
		string[] parts = filter.Split('|');
		var types = new List<FilePickerFileType>();
		for (int i = 0; i + 1 < parts.Length; i += 2)
		{
			string[] patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (!OperatingSystem.IsBrowser())
			{
				types.Add(new FilePickerFileType(parts[i]) { Patterns = patterns });
			}
			else if (patterns.Contains("*.*") || patterns.Contains("*"))
			{
				types.Add(FilePickerFileTypes.All);
			}
			else
			{
				types.Add(new FilePickerFileType(PatternInBrackets().Replace(parts[i], "").Trim())
				{
					Patterns = patterns,
					MimeTypes = patterns.Select(pattern => MediaTypeOf(Path.GetExtension(pattern))).Distinct().ToArray(),
				});
			}
		}
		return types;
	}

	/// <summary>The media type a browser's picker lists a file type under, by the extension: the registered one where there is one, a private one for this program's own files.</summary>
	private static string MediaTypeOf(string extension) => extension.ToLowerInvariant() switch
	{
		ProjectDocument.Extension => ProjectDocument.MimeType,
		ColorSchemeStore.XmlExtension => "application/x-wavedudenext-scheme",
		".xml" => "application/xml",
		".json" => "application/json",
		".csv" => "text/csv",
		".png" => "image/png",
		".jpg" or ".jpeg" => "image/jpeg",
		".bmp" => "image/bmp",
		".gif" => "image/gif",
		".tif" or ".tiff" => "image/tiff",
		".exr" => "image/x-exr",
		".mp4" => "video/mp4",
		".mpg" => "video/mpeg",
		".avi" => "video/x-msvideo",
		".mov" => "video/quicktime",
		_ => "application/octet-stream",
	};

	/// <summary>The pattern a filter's label carries in brackets at its end, " (*.wdproj)".</summary>
	[GeneratedRegex(@"\s*\([^()]*\*[^()]*\)\s*$")]
	private static partial Regex PatternInBrackets();

	/// <summary>The bytes of a picked file, whatever kind of file it is.</summary>
	public static async Task<byte[]> ReadAsync(IStorageFile file)
	{
		// into memory first: a browser's stream answers only asynchronously, and cannot say its length beforehand
		using var contents = new MemoryStream();
		await using (Stream stream = await file.OpenReadAsync())
		{
			await stream.CopyToAsync(contents);
		}
		return contents.ToArray();
	}

	/// <summary>
	/// A path a picked file can be read at, for a reader that takes a path: the file's own on a desktop; for a file
	/// without one, a copy in a folder of its own under the runtime's temporary folder, under the file's name, which
	/// the caller deletes when it has read it (<paramref name="isCopy"/>).
	/// </summary>
	public static async Task<(string Path, bool IsCopy)> LocalPathAsync(IStorageFile file)
	{
		if (file.TryGetLocalPath() is { } path)
		{
			return (path, false);
		}
		string folder = Path.Combine(Path.GetTempPath(), "WaveDudeNext", "picked", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		string copy = Path.Combine(folder, Path.GetFileName(file.Name));
		await File.WriteAllBytesAsync(copy, await ReadAsync(file));
		return (copy, true);
	}

	/// <summary>
	/// Writes <paramref name="contents"/> as the whole of a picked file, replacing what it held; done when the file is
	/// closed. Asynchronously throughout: a browser's stream takes no synchronous write, and only its awaited close
	/// commits the file. And always a whole array, never a part of one: the browser head's script, which stands in
	/// for a fault of Avalonia.Browser's write there (main.js), is handed the array without the part's bounds.
	/// </summary>
	public static async Task WriteAsync(IStorageFile file, byte[] contents)
	{
		await using Stream stream = await file.OpenWriteAsync();
		await stream.WriteAsync(contents, 0, contents.Length);
	}

	/// <summary>Writes the file at <paramref name="source"/> as the whole of a picked file, a few megabytes at a time, each a whole array (see <see cref="WriteAsync"/>); returns its length.</summary>
	public static async Task<long> CopyAsync(string source, IStorageFile target)
	{
		await using Stream output = await target.OpenWriteAsync();
		await using FileStream input = File.OpenRead(source);
		byte[] buffer = new byte[4 << 20];
		long total = 0;
		int read;
		while ((read = await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
		{
			byte[] chunk = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
			await output.WriteAsync(chunk, 0, chunk.Length);
			total += read;
		}
		return total;
	}
}
