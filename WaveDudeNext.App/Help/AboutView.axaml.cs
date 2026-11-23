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

using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using NLog;
using WaveDudeNext.App.Dialogs;
using WaveDudeNext.UI;

namespace WaveDudeNext.App.Help;

/// <summary>
/// The About dialog (Help > About), as upstream's: product, version and copyright from <see cref="AboutInfo"/>, the
/// credits as in the source header with their links, the license sentence with LICENSE.md, as the build converts it
/// (<see cref="HelpDocument"/>), shown on request, and the system report with a Copy button. Closed by Esc or
/// Enter; every link and button is in the Tab cycle, the focus starts on Close. The dialog's content
/// (<see cref="IDialogView"/>, 2026-11-15):
/// <see cref="Show"/> puts it into a modal <see cref="AboutWindow"/> owned by the main window on a desktop, and over
/// the main view where there are no windows (the browser head). UI thread only.
/// </summary>
public partial class AboutView : UserControl, IDialogView
{
	private static readonly Logger Logger = LogManager.GetLogger(typeof(AboutView).FullName!);
	/// <summary>The document box's height while its frame is sized to the content (the XAML value).</summary>
	private const double LicenseBoxHeight = 260;
	/// <summary>The grid row of the license document and of the filler above the buttons (the XAML's RowDefinitions).</summary>
	private const int LicenseRow = 3, FillerRow = 5;
	private readonly string _report;
	private HelpDocument? _license;

	/// <summary>Builds the dialog and gathers the system report once.</summary>
	public AboutView()
	{
		InitializeComponent();
		productText.Text = AboutInfo.Product;
		versionText.Text = string.Format(DialogStrings.Lookup("AboutVersionFormat"), AboutInfo.Version);
		copyrightText.Text = AboutInfo.Copyright;
		creditVleggaar.Text = AboutInfo.CreditVleggaar;
		creditWaveSimulator2D.Text = AboutInfo.CreditWaveSimulator2D;
		creditPort.Text = AboutInfo.CreditPort;
		_report = AboutInfo.SystemReport(Settings.Default.UseGpu, DialogStrings.Lookup);
		systemText.Text = _report;
	}

	/// <summary>Whether the license text is shown.</summary>
	public bool IsLicenseShown => licenseBox.IsVisible;

	/// <inheritdoc />
	public event EventHandler? CloseRequested;

	/// <summary>
	/// The license document was shown or hidden (<see cref="IsLicenseShown"/>), its row and the filler's set for it:
	/// the frame decides how the document's box is sized (<see cref="FillLicense"/>), a window by its own sizing.
	/// </summary>
	public event EventHandler? LicenseToggled;

	/// <summary>
	/// Shows the dialog modally for <paramref name="anchor"/>, a control of the main view: in a window over the
	/// anchor's, or over the main view where the anchor is in no window. Completes when the dialog has closed.
	/// </summary>
	public static async Task Show(Control anchor)
	{
		if (TopLevel.GetTopLevel(anchor) is Window owner)
		{
			await new AboutWindow().ShowDialog(owner);
		}
		else if (DialogOverlay.Of(anchor) is { } overlay)
		{
			// the overlay's frame is as high as the content: the license document keeps its box's fixed height there,
			// and the frame scrolls where the page is lower than the dialog with it
			await overlay.ShowAsync(new AboutView());
		}
		else
		{
			Logger.Warn("About cannot be shown: its view is not on screen");
		}
	}

	/// <inheritdoc />
	public void Opened() => closeButton.Focus();

	/// <summary>Esc closes even while the text box has the focus; a bare letter presses the button it underlines.</summary>
	public void FrameKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			CloseRequested?.Invoke(this, EventArgs.Empty);
		}
		else if (DialogStrings.TryPressAccessLetter(this, e))
		{
			e.Handled = true;
		}
	}

	/// <summary>
	/// How the license document's box is sized: filling its row, which is the star row while the document is shown
	/// (a frame that gives the dialog more height than its content needs), or at its fixed height, as a frame sized
	/// to the content needs it.
	/// </summary>
	public void FillLicense(bool fill) => licenseBox.Height = fill ? double.NaN : LicenseBoxHeight;

	/// <summary>
	/// Opens an address in the user's browser: through the desktop (xdg-open, ShellExecute), or, where the application
	/// is itself a page in a browser, which has no process to start, through the top level <paramref name="origin"/>
	/// is in, in a new tab. A failure is logged, never shown.
	/// </summary>
	internal static void OpenInBrowser(Control origin, string url)
	{
		Logger.Debug("About: opening {Uri}", url);
		if (OperatingSystem.IsBrowser())
		{
			_ = LaunchAsync(origin, url);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
		{
			Logger.Warn(ex, "About: could not open {Uri}", url);
		}
	}

	/// <summary>The browser opens the address itself; whatever it refuses with is logged, a link being no reason to end the program.</summary>
	private static async Task LaunchAsync(Control origin, string url)
	{
		try
		{
			if (TopLevel.GetTopLevel(origin) is not { } top || !await top.Launcher.LaunchUriAsync(new Uri(url)))
			{
				Logger.Warn("About: the browser did not open {Uri}", url);
			}
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "About: could not open {Uri}", url);
		}
	}

	/// <summary>A credit or license link: the address for its automation id, opened in the browser.</summary>
	private void OnLink(object? sender, RoutedEventArgs e)
	{
		string? url = sender is Control control ? AutomationProperties.GetAutomationId(control) switch
		{
			"AboutLinkHuygensYouTube" => AboutInfo.HuygensYouTube,
			"AboutLinkHuygensSite" => AboutInfo.HuygensSite,
			"AboutLinkDiffractionYouTube" => AboutInfo.DiffractionYouTube,
			"AboutLinkWaveSimulator2D" => AboutInfo.WaveSimulator2D,
			"AboutLinkGpl" => AboutInfo.Gpl,
			_ => null,
		} : null;
		if (url != null)
		{
			OpenInBrowser(this, url);
		}
	}

	private void OnToggleLicense(object? sender, RoutedEventArgs e)
	{
		bool show = !licenseBox.IsVisible;
		if (show && _license == null)
		{
			_license = HelpDocument.Load(HelpDocument.License);
			_license.LinkActivated += (_, args) =>
			{
				if (!args.Target.StartsWith('#'))
				{
					OpenInBrowser(this, args.Target);
				}
			};
			licenseView.Content = _license.Root;
		}
		licenseBox.IsVisible = show;
		// the extra height of a frame larger than the content goes to the document while it is shown, else to the gap
		// above the buttons
		layout.RowDefinitions[LicenseRow].Height = show ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
		layout.RowDefinitions[FillerRow].Height = show ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
		licenseToggle[!ContentProperty] = new DynamicResourceExtension(show ? "AboutHideLicense" : "AboutShowLicense");
		LicenseToggled?.Invoke(this, EventArgs.Empty);
	}

	private async void OnCopy(object? sender, RoutedEventArgs e)
	{
		try
		{
			Avalonia.Input.Platform.IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
			if (clipboard == null)
			{
				Logger.Warn("About: no clipboard on this platform");
				return;
			}
			await clipboard.SetTextAsync(_report);
			Logger.Info(DialogStrings.Lookup("AboutCopied"));
		}
		catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
		{
			// held by another program on a desktop; refused by the browser where the application is a page
			Logger.Warn(ex, "About: the clipboard could not be written");
		}
	}

	private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
