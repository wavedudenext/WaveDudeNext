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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NLog;
using WaveDudeNext.App.Commands;
using WaveDudeNext.App.Dialogs;
using WaveDudeNext.App.Docking;
using WaveDudeNext.App.Localization;
using WaveDudeNext.App.Logging;
using WaveDudeNext.App.Platform;
using WaveDudeNext.App.Theming;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;
using WaveDudeNext.UI;

namespace WaveDudeNext.App;

/// <summary>
/// The main view of the Avalonia head: the menu bar, the dock host with the Project and Properties panes around the
/// preview, the status bar. Like the WPF window, it is its own data context and holds the application's models of
/// <c>WaveDudeNext.UI</c>: the open project as a <see cref="Document"/>, what is selected as a <see cref="Selection"/>, the
/// current run as a <see cref="RunHost"/>, the live-editing rules as a <see cref="LivePreview"/>, the exports, the
/// brightness fit; the markup binds the panes to them, the menus to <see cref="Commands"/>, whose members are a line each
/// into a model. The view keeps what only the user interface can do: the dialogs, the dock layout in the settings, F10
/// for the menu bar, a dropped file. The five interfaces the models need are the Avalonia pieces under Platform/.
/// <para>
/// A view and not a window (2026-11-12, for the browser head, which has no windows): the desktop head's
/// <see cref="MainWindow"/> hosts it and keeps the title, the placement, the closing and the zoom; the browser head
/// shows it as its single view. What needs the top level it sits in (the key bindings, which must work with the focus
/// anywhere, F10, a window's activation) is put there when the view joins it. The dialogs (the message box, the
/// memory warning, About, the manual) are views too, each shown in a window of its own over the view's window and,
/// where there are no windows, over the view itself, in the <see cref="DialogOverlay"/> the markup holds above the
/// content (2026-11-15, the user's design: the same markup for both heads); Exit closes the view's window and does
/// nothing where there is none.
/// </para>
/// </summary>
public partial class MainView : UserControl
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(MainView).FullName!);

	private readonly ProjectItems _items;
	private readonly ObjectClipboard _clipboard;
	private readonly UndoHistory _history;
	private readonly ExportPaths _exportPaths;
	private readonly AvaloniaExportDialogs _exportDialogs;
	private readonly EditBoundaries _boundaries;
	/// <summary>
	/// The picked file the open project was read from or last written to, where files have no paths (the browser head):
	/// File > Save writes there again, as it writes to the document's file name on a desktop. Null for a project with a
	/// path, and for one that came from no file.
	/// </summary>
	private IStorageFile? _projectFile;
	private readonly List<KeyBinding> _keyBindings = [];
	private TopLevel? _topLevel;

	/// <summary>Creates the view: the models and their wiring as the WPF window has them, the markup, the commands, the menus, the layout from the settings.</summary>
	public MainView()
	{
		var decoder = new SkiaImageDecoder();
		SceneImage = new SceneImageFile(Document, decoder, new AvaloniaSynchronizationContext(DispatcherPriority.Normal));
		// the embedded image replaced by its changed file is an edit complete in itself: it is finished when it arrives
		SceneImage.Reembedded += (_, _) => OnEditFinished();
		// the scene is composed when the UI thread is idle, so the changes of one drag step make one composition
		SceneComposer = new SceneComposer(Document, decoder, new AvaloniaSynchronizationContext(DispatcherPriority.Background));
		_exportPaths = new ExportPaths(Document);
		_items = new ProjectItems(Document, Selection);
		_clipboard = new ObjectClipboard(Document, Selection, _items, new AvaloniaClipboard(this));
		_history = new UndoHistory(Document, Selection);
		_history.PropertyChanged += (_, _) => Commands.Requery();
		// a step written back by Undo or Redo: the colours of the project as it is now (see OnProjectChanged)
		_history.Restored += (_, _) => Preview.RefreshColors();
		// the run's progress lands on the UI thread at Loaded priority, as in the WPF head (2026-11-04): ahead of queued input
		RunHost = new RunHost(Document, SceneComposer, new AvaloniaMemoryWarning(this), new AvaloniaSynchronizationContext(DispatcherPriority.Loaded));
		RunHost.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName is nameof(RunHost.Run) or nameof(RunHost.IsStarting))
			{
				Commands.Requery();
			}
		};
		RunHost.RunStateChanged += (_, _) => Commands.Requery();
		Live = new LivePreview(Document, RunHost, Preview, Busy, SceneComposer, new AvaloniaSynchronizationContext(DispatcherPriority.Normal));
		_exportDialogs = new AvaloniaExportDialogs(this);
		Exporter = new Exporter(Document, RunHost, Preview, Busy, SceneComposer, _exportPaths, _exportDialogs, new SkiaFrameDecorators(), new AvaloniaSynchronizationContext(DispatcherPriority.Normal));
		Exporter.PropertyChanged += (_, _) => Commands.Requery();
		Exporter.Failed += OnExportFailed;
		// the auto re-run held back during the export may be due now; and what the export wrote for a picked file
		// without a path (the browser head) goes into that file
		Exporter.Finished += (_, _) =>
		{
			Live.RunFinished();
			Fire(DeliverExportAsync());
		};
		AutoBrightness = new AutoBrightness(Document, RunHost, Preview, Busy, SceneComposer, new AvaloniaSynchronizationContext(DispatcherPriority.Normal));
		AutoBrightness.PropertyChanged += (_, _) => Commands.Requery();
		// a fit that computed its frame arrived after the command that asked for it: an edit complete in itself
		AutoBrightness.Fitted += (_, _) => OnEditFinished();
		// the document's moments, wired before anything is loaded; its first, empty project is taken as a replacement was
		Document.ProjectReplacing += (_, _) => DiscardRun();
		Document.ProjectReplaced += (_, _) => OnProjectReplaced();
		Document.Changed += OnProjectChanged;
		Document.Opened += OnDocumentOpened;
		Document.Saved += (_, _) =>
		{
			_history.MarkSaved();
			SceneImage.Watch();
		};
		Document.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(ProjectDocument.Filename))
			{
				// File > Close is offered for a project with a file
				Commands.Requery();
			}
		};
		Selection.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(Selection.Current))
			{
				// most commands act on the current item
				Commands.Requery();
			}
		};
		Preview.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(PreviewState.IsPlaying))
			{
				Commands.Requery();
			}
		};
		OnProjectReplaced();
		DataContext = this;
		InitializeComponent();
		dialogOverlay.PropertyChanged += (_, args) =>
		{
			if (args.Property == DialogOverlay.IsOpenProperty)
			{
				OnDialogsChanged();
			}
		};
		// an edit has finished when the view's input says so: the undo step ends, the stale run is redone
		_boundaries = new EditBoundaries(this, OnEditFinished);
		WireCommands();
		BuildMenus();
		AttachStatusBar();
		RestoreLayout();
		Settings.Default.PropertyChanged += OnSettingChanged;
		if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
		{
			// a head without a window (the browser's) saves as things change, see SaveSettingsSoon
			Settings.Default.PropertyChanged += (_, _) => SaveSettingsSoon();
			dockHost.LayoutChanged += (_, _) => SaveSettingsSoon();
			dockHost.SplitterMoved += (_, _) => SaveSettingsSoon();
		}
		LanguageController.Instance.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(LanguageController.Current))
			{
				ApplyLanguageTexts();
			}
		};
		AddHandler(DragDrop.DropEvent, OnDrop);
	}

	/// <summary>The window this view is in, which Exit closes; null where the platform has no windows (the browser) and before the view is shown.</summary>
	private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

	/// <summary>
	/// The view has joined its top level, the desktop head's window or the browser's root: the key bindings go there, so
	/// they work wherever the focus is (and with none), as does F10; a window's activation looks at the scene image's file.
	/// </summary>
	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (_topLevel != null || TopLevel.GetTopLevel(this) is not { } top)
		{
			return;
		}
		_topLevel = top;
		top.KeyBindings.AddRange(_keyBindings);
		top.KeyDown += OnTopLevelKeyDown;
		if (top is Window window)
		{
			window.Activated += OnWindowActivated;
		}
	}

	/// <inheritdoc />
	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		if (_topLevel is not { } top)
		{
			return;
		}
		_topLevel = null;
		foreach (KeyBinding binding in _keyBindings)
		{
			top.KeyBindings.Remove(binding);
		}
		top.KeyDown -= OnTopLevelKeyDown;
		if (top is Window window)
		{
			window.Activated -= OnWindowActivated;
		}
	}

	/// <summary>Back from the image editor: the scene image's file is looked at, should an event of its change have gone missing.</summary>
	private void OnWindowActivated(object? sender, EventArgs e) => SceneImage.CheckFile();

	/// <summary>The open project, its file and whether it was edited; the markup binds the project and the title there.</summary>
	public ProjectDocument Document { get; } = new();

	/// <summary>The embedded scene image and its file on disk.</summary>
	public SceneImageFile SceneImage { get; }

	/// <summary>The scene pixels the preview, the runs and the exports take.</summary>
	public SceneComposer SceneComposer { get; }

	/// <summary>The current item and the selected list; the tree, the preview and the properties pane bind them.</summary>
	public Selection Selection { get; } = new();

	/// <summary>What the application is busy with, for the status bar and the preview's dimming.</summary>
	public BusyState Busy { get; } = new();

	/// <summary>The preview as the application sees it: the position, playback, the canvas shown.</summary>
	public PreviewState Preview { get; } = new();

	/// <summary>The current run and its store.</summary>
	public RunHost RunHost { get; }

	/// <summary>The live-editing rules: the stale run, the scratch frame, the automatic re-run.</summary>
	public LivePreview Live { get; }

	/// <summary>The exports.</summary>
	public Exporter Exporter { get; }

	/// <summary>The pictures' Auto brightness.</summary>
	public AutoBrightness AutoBrightness { get; }

	/// <summary>The commands the menus, the panes and the overlays bind; each a line into a model (<see cref="WireCommands"/>).</summary>
	public AppCommands Commands { get; } = new();

	/// <summary>The system's own font where the head offers it (<see cref="App.SystemFont"/>), for the View menu's entry; null in the desktop head.</summary>
	public ISystemFont? SystemFont => App.SystemFont;

	/// <summary>An edit has finished (the window's input said so, or an edit complete in itself arrived): the undo step ends, the stale run is redone.</summary>
	private void OnEditFinished()
	{
		_history.EndStep();
		Live.EditFinished();
	}

	/// <summary>Opens a file of any kind the document reads (a project, a Wave Dude 2D configuration, a colour scheme); an error is shown.</summary>
	internal async Task OpenFileAsync(string fileName)
	{
		if (!Document.TryOpen(fileName, out Exception? error))
		{
			await ShowErrorAsync(string.Format(Text("OpenError"), fileName), error);
		}
	}

	/// <summary>A new project: the selection, the scene and the export paths start over, the undo history with it, and the live rules last, so their first frame follows the new scene.</summary>
	private void OnProjectReplaced()
	{
		_projectFile = null;
		Selection.Items.Clear();
		SceneComposer.Reset();
		_exportPaths.Forget();
		SceneComposer.Update();
		_history.Start(Document.Project);
		Live.ProjectReplaced();
		Commands.Requery();
	}

	private void OnDocumentOpened(object? sender, OpenedEventArgs e)
	{
		if (e.Kind == OpenedKind.ProjectFile)
		{
			_exportPaths.Restore();
		}
		Selection.Reveal(e.Item);
		if (e.Kind == OpenedKind.ProjectFile)
		{
			SceneImage.Sync("changed since the project was saved");
		}
		else if (e.Kind == OpenedKind.ImportedProject)
		{
			SceneImage.Sync("imported");
		}
	}

	/// <summary>The project is being replaced: an export reading the run's store ends with what it has, then the run goes.</summary>
	private void DiscardRun()
	{
		if (Exporter.IsWriting)
		{
			Logger.Warn("Export cancelled: its run is being discarded");
			Exporter.Cancel();
		}
		_ = RunHost.Discard();
	}

	/// <summary>The dock layout goes into the settings, for the host to save: the desktop window calls this when it closes.</summary>
	internal void SaveLayout() => Settings.Default.Layout = dockHost.SaveLayout();

	private bool _settingsSavePending;
	private bool _savingSettings;

	/// <summary>
	/// Saves the settings, the dock layout with them, at the UI thread's next idle moment; several changes made
	/// together are one save. For a head without a window (the browser's, 2026-11-14, with its settings kept in the
	/// browser's local storage): a window saves all of this as it closes, a page is left without the application
	/// hearing of it in time, so there a changed setting (the theme, the language, the pictures' orientation as much
	/// as what the menus' commands save themselves), a changed dock arrangement and a splitter between the panes
	/// moved are saved when they change, the splitter when its drag has finished (the user's word the same evening:
	/// it had waited for the next thing that changed). The idle moment comes after the layout has run, so the pane
	/// sizes read here are the arranged ones. The layout set here is itself a changed setting, which does not ask
	/// for another save.
	/// </summary>
	private void SaveSettingsSoon()
	{
		if (_settingsSavePending || _savingSettings)
		{
			return;
		}
		_settingsSavePending = true;
		Dispatcher.UIThread.Post(() =>
		{
			_settingsSavePending = false;
			_savingSettings = true;
			try
			{
				SaveLayout();
				Settings.Default.TrySave();
			}
			finally
			{
				_savingSettings = false;
			}
		}, DispatcherPriority.Background);
	}

	/// <summary>The view's host is closing: the export, the run, the live rules and the scene image's watch end, and the status bar leaves the log.</summary>
	internal void Shutdown()
	{
		Exporter.Shutdown();
		RunHost.Shutdown();
		Live.Shutdown();
		SceneImage.Stop();
		StatusBarTarget.Detach(ShowStatusLine);
	}

	// ---- the dialogs the view shows

	/// <summary>The save question for an edited project: true when the work may go on (saved, or discarded), false for Cancel or a failed save.</summary>
	internal async Task<bool> ConfirmDiscardAsync()
	{
		if (!Document.IsDirty)
		{
			return true;
		}
		if (Environment.GetEnvironmentVariable("WAVEDUDE_DISCARD_EDITS") == "1")
		{
			// a UI harness cannot answer the question by posted keys: it discards
			Logger.Debug("Edits discarded without asking (WAVEDUDE_DISCARD_EDITS)");
			return true;
		}
		// in the application's own message box: a window over the view's on a desktop, over the view itself in a browser
		string name = Document.Filename != null ? Path.GetFileName(Document.Filename) : Text("Untitled");
		MessageAnswer answer = await MessageView.Show(this, string.Format(Text("SaveChangesFormat"), name), MessageKind.Question, MessageButtons.SaveDiscardCancel);
		return answer switch
		{
			MessageAnswer.Save => await SaveAsync(),
			MessageAnswer.Discard => true,
			_ => false,
		};
	}

	/// <summary>Shows an error in the application's message box, with the exception's own words on a second line.</summary>
	private Task ShowErrorAsync(string message, Exception? error)
	{
		string text = error == null ? message : message + Environment.NewLine + (error is RunSpecException { Key: { } key } && this.TryFindResource(key, out object? reason) && reason is string words ? words : error.Message);
		return MessageView.Show(this, text, MessageKind.Error, MessageButtons.Ok);
	}

	/// <summary>The file pickers of the top level the view is in; null before it is shown.</summary>
	private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

	/// <summary>
	/// Picks one file to open; null when cancelled. A null title leaves the platform's own. The file has a path on a
	/// desktop; a browser hands a page the file's name and contents alone (<see cref="OpenPickedAsync"/>).
	/// </summary>
	private async Task<IStorageFile?> PickOpenFileAsync(string? title, IReadOnlyList<FilePickerFileType> types, string? startFolder)
	{
		if (Storage is not { } storage)
		{
			return null;
		}
		IReadOnlyList<IStorageFile> files = await PickedFiles.OpenAsync(storage, new FilePickerOpenOptions
		{
			Title = title,
			AllowMultiple = false,
			FileTypeFilter = types,
			SuggestedStartLocation = await StartFolderAsync(storage, startFolder),
		});
		return files.Count > 0 ? files[0] : null;
	}

	/// <summary>
	/// The file types File > Open offers: the string table's list (projects, Wave Dude 2D projects, XML files, all
	/// files); in a browser the projects' entry alone, a page reading project files alone so far
	/// (<see cref="OpenPickedAsync"/>).
	/// </summary>
	private IReadOnlyList<FilePickerFileType> OpenFileTypes()
	{
		IReadOnlyList<FilePickerFileType> types = PickedFiles.TypesFrom(Text("OpenFileDialogFilter"));
		return OperatingSystem.IsBrowser() && types.Count > 0 ? [types[0]] : types;
	}

	/// <summary>
	/// Picks a file to write; null when cancelled. The file has a path on a desktop; in a browser it is written
	/// through the picked file itself (<see cref="PickedFiles"/>).
	/// </summary>
	private async Task<IStorageFile?> PickSaveFileAsync(string? title, string filter, string suggestedName, string? defaultExtension, string? startFolder)
	{
		if (Storage is not { } storage)
		{
			return null;
		}
		IReadOnlyList<FilePickerFileType> types = PickedFiles.TypesFrom(filter);
		return await PickedFiles.SaveAsync(storage, new FilePickerSaveOptions
		{
			Title = title,
			SuggestedFileName = suggestedName,
			DefaultExtension = defaultExtension?.TrimStart('.'),
			FileTypeChoices = types,
			SuggestedFileType = types.Count > 0 ? types[0] : null,
			ShowOverwritePrompt = true,
			SuggestedStartLocation = await StartFolderAsync(storage, startFolder),
		});
	}

	private static async Task<IStorageFolder?> StartFolderAsync(IStorageProvider storage, string? folder) =>
		folder != null && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null;

	/// <summary>
	/// A string of the table by key; the key itself when the table lacks it. The table is the application's: a view that
	/// has not joined a window yet (its constructor names the dock's panes) does not reach it through its own parents,
	/// as a window does, so the application is asked then.
	/// </summary>
	private string Text(string key) =>
		(this.TryFindResource(key, out object? value) || Application.Current?.TryFindResource(key, out value) == true) && value is string text ? text : key;

	// ---- the file commands

	private async Task NewAsync()
	{
		if (await ConfirmDiscardAsync())
		{
			Document.New();
		}
	}

	private async Task OpenAsync()
	{
		IStorageFile? file = await PickOpenFileAsync(null, OpenFileTypes(), Document.Folder);
		if (file != null && await ConfirmDiscardAsync())
		{
			await OpenPickedAsync(file);
		}
	}

	/// <summary>
	/// Opens a file the user picked or dropped: by its path where it has one (a desktop), as any kind the document
	/// reads; else from its contents (2026-11-15, the user's choice for the browser head, whose picker and drops hand a
	/// page a file's name and bytes and no path), which so far reads project files alone: the project is unsaved then,
	/// its file's name suggested, as an example's is. An error is shown.
	/// </summary>
	private async Task OpenPickedAsync(IStorageFile file)
	{
		if (file.TryGetLocalPath() is { } path)
		{
			await OpenFileAsync(path);
			return;
		}
		await OpenContentsAsync(file.Name, () => PickedFiles.ReadAsync(file), file);
	}

	/// <summary>
	/// Opens a project from a file's name and contents, read by <paramref name="read"/> only once the name has passed:
	/// <see cref="OpenPickedAsync"/>'s second half, and a dropped file's way in where a drop is read by the head
	/// itself (<see cref="App.PageDrops"/>). <paramref name="file"/> is the picked file File > Save writes back
	/// into; null for a dropped one, which has no such place, so its project is saved through the dialog.
	/// </summary>
	private async Task OpenContentsAsync(string name, Func<Task<byte[]>> read, IStorageFile? file)
	{
		Exception? error;
		try
		{
			// the bytes first: the project's reader reads as it goes, and a browser's stream answers only asynchronously
			if (Document.TryOpen(await read(), name, out error) && !ProjectDocument.IsColorSchemeName(name))
			{
				// File > Save writes back into this file; a colour scheme's file went into the open project, whose file stays
				_projectFile = file;
			}
		}
		catch (Exception ex)
		{
			// whatever the platform refuses the file with (a browser's errors are its own kind): shown, not fatal
			Logger.Error(ex, "Error while reading '{Name}'", name);
			error = ex;
		}
		if (error != null)
		{
			await ShowErrorAsync(string.Format(Text("OpenError"), name), error);
		}
	}

	private async Task OpenRecentAsync(string path)
	{
		if (await ConfirmDiscardAsync())
		{
			await OpenFileAsync(path);
		}
	}

	private async Task OpenExampleAsync(ExampleEntry example)
	{
		if (await ConfirmDiscardAsync() && !Document.TryOpenExample(example, out Exception? error))
		{
			await ShowErrorAsync(string.Format(Text("OpenError"), example.FileName), error);
		}
	}

	/// <summary>
	/// Save, or Save As for a project without a file; false when the user cancelled or the save failed. The file is
	/// the document's path on a desktop, and where files have no paths the picked file the project came from or was
	/// last saved to.
	/// </summary>
	private Task<bool> SaveAsync() => Document.Filename != null ? SaveFileAsync(Document.Filename) : _projectFile != null ? SaveToAsync(_projectFile) : SaveAsAsync();

	private async Task<bool> SaveAsAsync()
	{
		IStorageFile? file = await PickSaveFileAsync(null, Text("SaveFileDialogFilter"), Document.Filename != null ? Path.GetFileName(Document.Filename) : Document.SuggestedFileName ?? "", ProjectDocument.Extension, Document.Folder);
		return file != null && (file.TryGetLocalPath() is { } path ? await SaveFileAsync(path) : await SaveToAsync(file));
	}

	private async Task<bool> SaveFileAsync(string fileName)
	{
		if (Document.TrySave(fileName, out Exception? error))
		{
			return true;
		}
		await ShowErrorAsync(string.Format(Text("SaveError"), fileName), error);
		return false;
	}

	/// <summary>
	/// Saves the project into a picked file that has no path (the browser head, 2026-11-15): the project's bytes are
	/// written through the file itself, and the document is saved under the file's name, which File > Save then writes
	/// to again. An error is shown.
	/// </summary>
	private async Task<bool> SaveToAsync(IStorageFile file)
	{
		string name = file.Name;
		if (Document.TryGetContents(out byte[]? contents, out Exception? error))
		{
			try
			{
				await PickedFiles.WriteAsync(file, contents);
				Document.ContentsSaved(name);
				_projectFile = file;
				return true;
			}
			catch (Exception ex)
			{
				// whatever the platform refuses the file with (a browser's errors are its own kind): shown, not fatal
				Logger.Error(ex, "Error while saving '{Name}'", name);
				error = ex;
			}
		}
		await ShowErrorAsync(string.Format(Text("SaveError"), name), error);
		return false;
	}

	/// <summary>An export has ended: what it wrote for a picked file without a path goes into that file (<see cref="AvaloniaExportDialogs.DeliverAsync"/>); an error is shown.</summary>
	private async Task DeliverExportAsync()
	{
		if (await _exportDialogs.DeliverAsync() is { } error)
		{
			await ShowErrorAsync(Text("ExportError"), error);
		}
	}

	/// <summary>A probe's data as CSV: the export, which writes the file itself, and then the file's way to where the user picked.</summary>
	private async Task ExportProbeAsync(WaveDudeNext.Model.Probe probe)
	{
		await Exporter.ExportProbeAsync(probe);
		await DeliverExportAsync();
	}

	private async Task CloseProjectAsync()
	{
		if (await ConfirmDiscardAsync())
		{
			Document.New();
		}
	}

	private async Task BrowseSceneImageAsync()
	{
		IStorageFile? picked = await PickOpenFileAsync(Text("TipBrowseSceneImage"), PickedFiles.TypesFrom(Text("SceneImageFilter")), Document.Folder);
		if (picked?.TryGetLocalPath() is { } file)
		{
			SceneImage.TryEmbed(file);
		}
		else if (picked != null)
		{
			// a file without a path (the browser head): embedded from its bytes, known by its name alone
			try
			{
				SceneImage.TryEmbed(await PickedFiles.ReadAsync(picked), picked.Name);
			}
			catch (Exception ex)
			{
				// whatever the platform refuses the file with (a browser's errors are its own kind): logged, not fatal
				Logger.Error(ex, "The image could not be loaded: {Message}", ex.Message);
			}
		}
	}

	private async Task SaveSceneImageAsync()
	{
		if (!SceneImage.CanSave)
		{
			return;
		}
		string extension = SceneImage.SaveExtension;
		// the embedded file's own type; an unknown one gets the picker's list
		string filter = extension.Length > 0 ? string.Format(Text("SceneImageSaveFilterFormat"), extension) : Text("SceneImageFilter");
		IStorageFile? picked = await PickSaveFileAsync(Text("TipSaveSceneImage"), filter, SceneImage.SaveFileName, extension.Length > 0 ? extension : null, SceneImage.SaveFolder);
		if (picked == null)
		{
			Logger.Debug("Scene image save cancelled in the Save dialog");
			return;
		}
		Exception? error = null;
		if (picked.TryGetLocalPath() is { } file)
		{
			SceneImage.TrySave(file, out error);
		}
		else if (Document.Project.Scene.ImageData is { } bytes)
		{
			// a file without a path (the browser head): the embedded bytes written through the picked file itself
			try
			{
				await PickedFiles.WriteAsync(picked, bytes);
				Logger.Info("Scene image saved: {Size} KB to '{Name}'", Math.Max(1, bytes.Length / 1024), picked.Name);
			}
			catch (Exception ex)
			{
				// whatever the platform refuses the file with (a browser's errors are its own kind): shown, not fatal
				Logger.Error(ex, "The scene image could not be saved: {Message}", ex.Message);
				error = ex;
			}
		}
		if (error != null)
		{
			await ShowErrorAsync(string.Format(Text("SceneImageSaveError"), picked.TryGetLocalPath() ?? picked.Name), error);
		}
	}

	// ---- the keys and the drop the view handles itself

	/// <summary>The menu bar is out of the Tab cycle: F10 is the way in (the WPF head's rule). Menu.Open is Avalonia's answer to Alt and F10: the first header takes the keyboard focus, Down opens it, Left and Right move between headers.</summary>
	private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
	{
		if (!e.Handled && e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.None && !dialogOverlay.IsOpen)
		{
			menu.Open();
			e.Handled = true;
		}
	}

	/// <summary>
	/// A file dropped anywhere on the view opens it (after the save question), as the WPF window does: the first with a
	/// path on a desktop; a file without one, the first, from its contents (<see cref="OpenPickedAsync"/>). Not under
	/// a dialog over the view, which has the application to itself.
	/// </summary>
	private async void OnDrop(object? sender, DragEventArgs e)
	{
		List<IStorageItem>? items = e.DataTransfer.TryGetFiles()?.ToList();
		string formats = string.Join(", ", e.DataTransfer.Formats);
		Logger.Debug("Drop on the view: {Files} file(s), formats: {Formats}{Dialog}", items?.Count ?? 0, formats.Length > 0 ? formats : "none", dialogOverlay.IsOpen ? "; ignored under a dialog" : "");
		if (dialogOverlay.IsOpen)
		{
			return;
		}
		if (items is not { Count: > 0 })
		{
			return;
		}
		if (items.Select(item => item.TryGetLocalPath()).FirstOrDefault(path => path != null) is { } file)
		{
			if (await ConfirmDiscardAsync())
			{
				await OpenFileAsync(file);
			}
		}
		else if (items.OfType<IStorageFile>().FirstOrDefault() is { } contents && await ConfirmDiscardAsync())
		{
			await OpenPickedAsync(contents);
		}
	}

	// ---- the dialogs over the view (a head without windows)

	/// <summary>
	/// A dialog was opened over the view or the last one closed (<see cref="DialogOverlay"/>, the browser head's
	/// dialogs): while one is open the application under it is out of reach. The markup disables the content; here the
	/// view's key bindings leave the top level, where they would be tried before the dialog sees a key (Esc is Stop's),
	/// and come back when the dialogs have gone, and a menu bar that Alt opened under the dialog is closed again.
	/// </summary>
	private void OnDialogsChanged()
	{
		if (_topLevel is not { } top)
		{
			return;
		}
		if (dialogOverlay.IsOpen)
		{
			foreach (KeyBinding binding in _keyBindings)
			{
				top.KeyBindings.Remove(binding);
			}
		}
		else
		{
			top.KeyBindings.AddRange(_keyBindings);
			menu.Close();
		}
	}

	// ---- the dock layout

	/// <summary>The saved dock layout, else the default arrangement the markup declares; failing both, the two panes at their sides.</summary>
	private void RestoreLayout()
	{
		bool restored = Settings.Default.Layout is { } saved && dockHost.LoadLayout(saved);
		if (!restored && dockHost.DefaultLayout != null)
		{
			restored = dockHost.LoadLayout(dockHost.DefaultLayout);
		}
		if (!restored)
		{
			Logger.Warn("No dock layout could be loaded: the Project and Properties panes take their default sides");
			foreach (DockItem item in dockHost.Items)
			{
				if (item.Id is "project" or "properties")
				{
					dockHost.Show(item);
				}
			}
		}
	}
}
