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
using Avalonia.Threading;
using WaveDudeNext.App.Dialogs;
using WaveDudeNext.Export;
using WaveDudeNext.UI;

namespace WaveDudeNext.App.Platform;

/// <summary>
/// The export dialogs for the models of <c>WaveDudeNext.UI</c> (<see cref="IExportDialogs"/>): the platform's Save dialog
/// through Avalonia's storage provider with the title and the filters of the string table, its filter order being
/// <see cref="ExportFormats.All"/>'s, <see cref="ExportFormats.Stills"/>' for a still and one CSV filter for a probe's
/// data; and the application's message box for the question about a sequence folder. Both answer later, as
/// Avalonia's dialogs do; the models go on from the UI thread when they have. The top level is looked up when a
/// dialog is asked for: the storage provider is any top level's, and the message box is shown in a window over the
/// anchor's or, where there are no windows (the browser head), over the main view.
/// <para>
/// <b>Where a picked file has no path</b> (the browser head, 2026-11-15): the exports of the library write to paths,
/// so the model is given one in a folder of the runtime's own temporary file system, which stands for the pick, and
/// this class is the library's opener of export files (<see cref="ExportFiles.Opener"/>, set here): a file an export
/// makes under such a path is the picked file itself, written through the platform's own stream as the export goes,
/// a few megabytes at a time (<see cref="PickedExportFile"/>). So an export never lies in memory on its way out,
/// which the temporary file system of a browser is (the user's point: a browser is always short of room, and a
/// video need not fit it). An export of several files, both layers or an image sequence, cannot go into one picked
/// file: the user is asked for a folder as well (a second dialog, which a browser opens only on a click, hence the
/// question between the two), and the files are made there as a desktop lays them out, a sequence in a folder of its
/// own. A pick is kept for Export last, which writes to the same path again.
/// </para>
/// <para>
/// What is written on the UI thread, a probe's data, cannot wait for that thread and is small: it is written at the
/// temporary path, and when the export has ended the application calls <see cref="DeliverAsync"/>, which copies it
/// into the picked file.
/// </para>
/// </summary>
public sealed class AvaloniaExportDialogs : IExportDialogs
{
	private static readonly NLog.Logger Logger = NLog.LogManager.GetLogger(typeof(AvaloniaExportDialogs).FullName!);

	/// <summary>
	/// What an export writes under <see cref="Folder"/> goes into <see cref="File"/>, the one picked file, or, where
	/// it writes several files, into <see cref="Target"/>, whose folders made so far are <see cref="Folders"/>, by
	/// their path within <see cref="Folder"/> (UI thread).
	/// </summary>
	private sealed record Delivery(string Folder, IStorageFile? File, IStorageFolder? Target)
	{
		public Dictionary<string, IStorageFolder> Folders { get; } = new(StringComparer.Ordinal);
	}

	private readonly Control _anchor;
	/// <summary>The picks without a path, the newest last; locked, an export's threads look their files up here.</summary>
	private readonly List<Delivery> _deliveries = [];

	/// <param name="anchor">A control of the view the dialogs belong to, whose resources hold the strings.</param>
	public AvaloniaExportDialogs(Control anchor)
	{
		_anchor = anchor;
		ExportFiles.Opener = OpenPicked;
	}

	/// <inheritdoc/>
	public async Task<SavePick?> AskWhereToSaveAsync(ExportSubject subject, string folder, string name, IReadOnlyList<ExportFormat> formats, int formatIndex)
	{
		IReadOnlyList<FilePickerFileType> types = PickedFiles.TypesFrom(Text(subject switch
		{
			ExportSubject.Still => "ExportDialogFilterStill",
			ExportSubject.ProbeData => "ExportDialogFilterCsv",
			_ => "ExportDialogFilter",
		}));
		int preset = Math.Clamp(formatIndex, 0, Math.Max(0, types.Count - 1));
		if (TopLevel.GetTopLevel(_anchor)?.StorageProvider is not { } storage)
		{
			return null;
		}
		// the types offered, each with its place among the formats; all of them, in the string table's order, on a desktop
		var offered = new List<(FilePickerFileType Type, int Index)>();
		for (int i = 0; i < types.Count; i++)
		{
			offered.Add((types[i], i));
		}
		if (OperatingSystem.IsBrowser())
		{
			// MP4 is offered where the browser hands the page an H.264 encoder (the head has set it then) and nowhere
			// else: nothing can be installed for a page, so there is no message to name a remedy, as a desktop's has. And
			// the browser's dialog starts on the first type, having no way to be told another, so the format chosen last
			// goes first
			if (!ExportFormats.IsAvailable(ExportFormat.Mp4))
			{
				offered.RemoveAll(entry => entry.Index < formats.Count && formats[entry.Index] == ExportFormat.Mp4);
			}
			if (offered.FindIndex(entry => entry.Index == preset) is > 0 and int at)
			{
				(FilePickerFileType Type, int Index) first = offered[at];
				offered.RemoveAt(at);
				offered.Insert(0, first);
			}
			if (offered.Count > 0 && offered.TrueForAll(entry => entry.Index != preset))
			{
				preset = offered[0].Index;
			}
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
			DefaultExtension = (formats.Count > 0 ? ExportFormats.Extension(formats[Math.Clamp(preset, 0, formats.Count - 1)]) : ProbeCsv.Extension).TrimStart('.'),
			FileTypeChoices = offered.Select(entry => entry.Type).ToList(),
			SuggestedFileType = offered.Find(entry => entry.Index == preset).Type,
			ShowOverwritePrompt = true,
			SuggestedStartLocation = folder.Length > 0 && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null,
		};
		IStorageFile? file = await PickedFiles.SaveAsync(storage, options);
		if (file == null)
		{
			return null;
		}
		// the filter the user chose: the platform's dialog does not say, so it is read from the extension typed or
		// appended; a name that says none of those offered keeps the one offered first
		string picked = file.TryGetLocalPath() ?? file.Name;
		int chosen = preset;
		if (formats.Count > 0 && ExportFormats.FromPath(picked) is { } format)
		{
			int index = Math.Max(0, formats.ToList().IndexOf(format));
			chosen = offered.Exists(entry => entry.Index == index) ? index : preset;
		}
		if (file.TryGetLocalPath() is { } path)
		{
			return new SavePick(path, chosen);
		}
		return await PickWithoutPathAsync(storage, subject, file, formats.Count > 0 ? formats[chosen] : null, chosen);
	}

	/// <summary>
	/// The pick for a file that has no path: a path in a fresh folder of the runtime's temporary file system, under
	/// the picked file's name, noted as standing for the picked file; for an export of several files, after the
	/// question and the folder dialog, as standing for the folder picked there. Null when the user gave up.
	/// </summary>
	private async Task<SavePick?> PickWithoutPathAsync(IStorageProvider storage, ExportSubject subject, IStorageFile file, ExportFormat? format, int chosen)
	{
		IStorageFolder? target = null;
		bool several = subject == ExportSubject.Both || (subject is ExportSubject.Field or ExportSubject.Intensity && format is { } run && ExportFormats.IsSequence(run));
		if (several)
		{
			// the picked file stands for the name and the format alone here; the browser made it, empty, when it was picked
			if (await MessageView.Show(_anchor, Text("ExportNeedsFolder"), MessageKind.Question, MessageButtons.YesNo) != MessageAnswer.Yes
				|| await PickedFiles.FolderAsync(storage, new FolderPickerOpenOptions { Title = Text("ExportDialogTitleBoth"), AllowMultiple = false }) is not [IStorageFolder folder, ..])
			{
				Logger.Debug("Export cancelled: no folder was chosen for its files");
				await RemoveAsync(file);
				return null;
			}
			target = folder;
			await RemoveAsync(file);
		}
		string temporary = Path.Combine(Path.GetTempPath(), "WaveDudeNext", "export", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temporary);
		lock (_deliveries)
		{
			_deliveries.Add(new Delivery(temporary, several ? null : file, target));
		}
		Logger.Debug("Export without a path: {Folder} stands for {Where}", temporary, target != null ? "the folder '" + target.Name + "'" : "'" + file.Name + "'");
		return new SavePick(Path.Combine(temporary, Path.GetFileName(file.Name)), chosen);
	}

	/// <summary>The pick without a path that <paramref name="path"/> stands for or lies under, the newest; null for a path that is a path. Any thread.</summary>
	private Delivery? DeliveryFor(string path)
	{
		lock (_deliveries)
		{
			return _deliveries.FindLast(entry => path.StartsWith(entry.Folder + Path.DirectorySeparatorChar, StringComparison.Ordinal));
		}
	}

	/// <inheritdoc/>
	public string Shown(string path) => DeliveryFor(path) switch
	{
		null => path,
		{ File: { } file } => file.Name,
		{ Target: var target } delivery => (target?.Name ?? "") + "/" + Path.GetRelativePath(delivery.Folder, path).Replace(Path.DirectorySeparatorChar, '/'),
	};

	/// <summary>
	/// The library's opener of export files (<see cref="ExportFiles.Opener"/>): for a path that stands for a pick
	/// without a path, the picked file itself, or the file of that name made in the picked folder, a sequence's in a
	/// folder of its own there; null for every other path, which is then a file of the file system, as on a desktop.
	/// Called on the thread that writes the file, which waits here for the UI thread, where the platform's files are
	/// made and opened. On the UI thread itself null: nothing can wait for that thread there, so what is written on
	/// it (a probe's data) is a file at the temporary path, which <see cref="DeliverAsync"/> hands over afterwards.
	/// </summary>
	private IExportFile? OpenPicked(string path)
	{
		if (Dispatcher.UIThread.CheckAccess() || DeliveryFor(path) is not { } delivery)
		{
			return null;
		}
		string name = delivery.File?.Name ?? Path.GetFileName(path);
		try
		{
			return OnUiThread(async () =>
			{
				IStorageFile file = delivery.File ?? await FileInAsync(delivery, path);
				// a sequence's files, thousands of them, are named in the log at Trace alone
				bool oneOfMany = delivery.File == null && Path.GetDirectoryName(path) != delivery.Folder;
				return new PickedExportFile(await file.OpenWriteAsync(), file.Name, oneOfMany);
			});
		}
		catch (Exception ex) when (ex is not IOException)
		{
			// whatever the platform refuses with (a browser's errors are its own kind), as the error a file gives
			throw new IOException($"'{name}' could not be opened for writing: {ex.Message}", ex);
		}
	}

	/// <summary>The file for <paramref name="path"/> in the folder a pick of several files stands for, made there, in the folders the path has below the pick's own, which are made when missing. UI thread.</summary>
	private static async Task<IStorageFile> FileInAsync(Delivery delivery, string path)
	{
		IStorageFolder target = delivery.Target ?? throw new IOException("The export has no folder for its files.");
		string within = Path.GetRelativePath(delivery.Folder, Path.GetDirectoryName(path)!);
		IStorageFolder? into = target;
		if (within != "." && !delivery.Folders.TryGetValue(within, out into))
		{
			into = target;
			foreach (string part in within.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
			{
				into = await into.CreateFolderAsync(part) ?? throw new IOException($"The folder '{part}' could not be made in '{into.Name}'.");
			}
			delivery.Folders[within] = into;
		}
		string name = Path.GetFileName(path);
		return await into!.CreateFileAsync(name) ?? throw new IOException($"The file '{name}' could not be made in '{into.Name}'.");
	}

	/// <summary>
	/// Does <paramref name="work"/> on the UI thread and waits for it to its end, for a thread of an export, which
	/// may wait; the work's error is thrown here. Never from the UI thread, which would wait for itself.
	/// </summary>
	private static T OnUiThread<T>(Func<Task<T>> work)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			throw new InvalidOperationException("An export's file is written by the export's own threads, not on the UI thread.");
		}
		var done = new TaskCompletionSource<T>();
		Dispatcher.UIThread.Post(async () =>
		{
			try
			{
				done.SetResult(await work());
			}
			catch (Exception ex)
			{
				done.SetException(ex);
			}
		});
		return done.Task.GetAwaiter().GetResult();
	}

	/// <summary>
	/// A picked file as an export writes it (<see cref="IExportFile"/>): each piece goes into the platform's stream
	/// at its place, on the UI thread, where a browser's file is written, and the export's thread waits until the
	/// platform has taken it, so no more than the piece in hand is ever in memory. Always a whole array (see
	/// <see cref="PickedFiles.WriteAsync"/>); the stream's own position is not trusted, every piece is sought to.
	/// Closing awaits the stream's end, with which a browser puts the file in its place.
	/// </summary>
	private sealed class PickedExportFile(Stream stream, string name, bool oneOfMany) : IExportFile
	{
		private long _bytes;
		private int _pieces;
		private long _waited;

		public void Write(long position, byte[] bytes)
		{
			Do("written", async () =>
			{
				stream.Seek(position, SeekOrigin.Begin);
				await stream.WriteAsync(bytes, 0, bytes.Length);
			});
			_bytes += bytes.Length;
			_pieces++;
		}

		public void SetLength(long length) => Do("sized", () =>
		{
			stream.SetLength(length);
			return Task.CompletedTask;
		});

		public void Close()
		{
			Do("closed", async () => await stream.DisposeAsync());
			Logger.Log(oneOfMany ? NLog.LogLevel.Trace : NLog.LogLevel.Debug, "Export file '{Name}' written as it was made: {Megabytes:0.0} MB in {Pieces} piece(s), {Seconds:0.00} s of it waiting for the platform's file",
				name, _bytes / 1048576.0, _pieces, System.Diagnostics.Stopwatch.GetElapsedTime(0, _waited).TotalSeconds);
		}

		private void Do(string done, Func<Task> work)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				OnUiThread(async () =>
				{
					await work();
					return true;
				});
			}
			catch (Exception ex) when (ex is not IOException)
			{
				throw new IOException($"'{name}' could not be {done}: {ex.Message}", ex);
			}
			finally
			{
				_waited += System.Diagnostics.Stopwatch.GetTimestamp() - started;
			}
		}
	}

	/// <summary>The empty file a browser's Save dialog made for a name that will not be written: taken away again where the browser lets a page do that.</summary>
	private static async Task RemoveAsync(IStorageFile file)
	{
		try
		{
			await file.DeleteAsync();
		}
		catch (Exception ex)
		{
			Logger.Debug("The empty file '{Name}' the Save dialog made stays: {Message}", file.Name, ex.Message);
		}
	}

	/// <summary>
	/// An export has ended, completed, cancelled or failed: what it wrote at the temporary path of a pick without a
	/// path, which is what was written on the UI thread (a probe's data; an export's own threads write into the
	/// picked file as they go, see <see cref="OpenPicked"/>), is copied where the user chose. Nothing to do where
	/// the picks have paths. The files leave the temporary folder afterwards; the folder and the pick stay for
	/// Export last.
	/// </summary>
	/// <returns>What kept the files from being handed over, already logged, for the application to show; null when nothing did.</returns>
	public async Task<Exception?> DeliverAsync()
	{
		Exception? error = null;
		Delivery[] deliveries;
		lock (_deliveries)
		{
			deliveries = [.. _deliveries];
		}
		foreach (Delivery delivery in deliveries)
		{
			string[] files = Directory.Exists(delivery.Folder) ? Directory.GetFiles(delivery.Folder, "*", SearchOption.AllDirectories) : [];
			if (files.Length == 0)
			{
				continue;
			}
			Array.Sort(files, StringComparer.Ordinal);
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				long bytes = 0;
				if (delivery.File is { } single)
				{
					bytes = await PickedFiles.CopyAsync(files[0], single);
				}
				else if (delivery.Target != null)
				{
					foreach (string file in files)
					{
						bytes += await PickedFiles.CopyAsync(file, await FileInAsync(delivery, file));
					}
				}
				Logger.Info("Export handed over: {Count} file(s), {Megabytes:0.0} MB, into {Where} in {Seconds:0.0} s", files.Length, bytes / 1048576.0,
					delivery.Target != null ? "the folder '" + delivery.Target.Name + "'" : "'" + delivery.File!.Name + "'", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
			}
			catch (Exception ex)
			{
				// whatever the platform refuses with (a browser's errors are its own kind): shown, not fatal
				Logger.Error(ex, "The exported files could not be handed over: {Message}", ex.Message);
				error = ex;
			}
			finally
			{
				foreach (string file in files)
				{
					File.Delete(file);
				}
			}
		}
		return error;
	}

	/// <inheritdoc/>
	public async Task<bool> AskToReplaceFolderAsync(string folder) =>
		await MessageView.Show(_anchor, string.Format(Text("ExportFolderExistsFormat"), folder), MessageKind.Question, MessageButtons.YesNo) == MessageAnswer.Yes;

	private string Text(string key) => _anchor.FindResource(key) as string ?? key;
}
