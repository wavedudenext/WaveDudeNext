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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NLog;

namespace WaveDudeNext.App.Help;

/// <summary>A link of a document was activated: "#anchor" for a heading, else a web or mail address.</summary>
/// <param name="target">The link's destination as written.</param>
/// <param name="title">The link's title, or null.</param>
public sealed class DocumentLinkEventArgs(string target, string? title) : EventArgs
{
	/// <summary>The link's destination as written: "#anchor", or an http, https or mailto address.</summary>
	public string Target { get; } = target;

	/// <summary>The link's title, or null.</summary>
	public string? Title { get; } = title;
}

/// <summary>
/// One of the documents the build converts from Markdown into Avalonia controls (<c>Build/Documents.targets</c>,
/// 2026-11-23, the user's choice: as the WPF head's documents are converted into FlowDocuments, and by the same
/// converter, <c>WaveDudeNext.WPF/Build/MarkdownToFlowDocument.cs</c>; until then this head parsed the Markdown and
/// the figures' SVGs each time a document was opened): the manual and the license. The build writes each as
/// Avalonia's XAML, and <see cref="Load"/> reads that file and makes the controls it names, as WPF reads its
/// compiled documents: the file is data, and what is read is the few kinds of element the converter writes
/// (<see cref="Create"/>, <see cref="Apply"/>, <see cref="Add"/>; anything else is an error naming it, the two ends
/// being out of step). Not compiled by Avalonia's XAML compiler, which was tried first: it makes one method of a
/// document, 745 KB of code for the manual, which the desktop head's first opening spent 0.9 s compiling.
/// The document is a <see cref="StackPanel"/> of the blocks: headings and paragraphs as selectable text blocks, lists
/// as rows of a marker and the item's blocks, quotes behind a bar, rules as lines, and figures as canvases over their
/// pictures in a <see cref="Viewbox"/> that only shrinks them. A heading's text block carries
/// <c>Tag="h{level}#{anchor}"</c>, the anchor GitHub's for the heading; a link is a button of the style class "link"
/// with its destination in its Tag, whose click raises <see cref="LinkActivated"/> for the view to act on. UI thread
/// (the document is controls); one instance for each view.
/// </summary>
public sealed partial class HelpDocument
{
	/// <summary>The manual, MANUAL.md.</summary>
	public static readonly Uri Manual = new("avares://WaveDudeNext.App/Help/Manual.xml");

	/// <summary>The license, LICENSE.md.</summary>
	public static readonly Uri License = new("avares://WaveDudeNext.App/Help/License.xml");

	private static readonly Logger Logger = LogManager.GetLogger(typeof(HelpDocument).FullName!);
	private readonly List<ManualHeading> _headings = [];
	/// <summary>The fonts and colours read so far by their text: a document names a handful of each, hundreds of times.</summary>
	private readonly Dictionary<string, FontFamily> _fonts = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IBrush> _brushes = new(StringComparer.Ordinal);
	/// <summary>What the figures' badges are drawn with, the same for every figure of the document.</summary>
	private readonly BadgeInk _ink = new();
	private int _figures;

	[GeneratedRegex(@"^h([1-6])#(.*)$")]
	private static partial Regex HeadingTag();

	private HelpDocument(Uri source)
	{
		Root = Read(source);
		Gather(Root);
		Root.AddHandler(Button.ClickEvent, OnClick);
	}

	/// <summary>The document: the blocks stacked, with the page padding of 10 px around them.</summary>
	public StackPanel Root { get; }

	/// <summary>Every heading in document order, the title included: what a link to "#anchor" is looked up in.</summary>
	public IReadOnlyList<ManualHeading> Headings => _headings;

	/// <summary>How many figures the document shows.</summary>
	public int FigureCount => _figures;

	/// <summary>A link in the text was activated: the view scrolls to the heading or opens the address.</summary>
	public event EventHandler<DocumentLinkEventArgs>? LinkActivated;

	/// <summary>
	/// A new copy of the document built under <paramref name="source"/> (<see cref="Manual"/>, <see cref="License"/>).
	/// A build without it, or with one this cannot read, gets one paragraph saying so, with the error logged.
	/// </summary>
	public static HelpDocument Load(Uri source) => new(source);

	/// <summary>The document's headings as a tree, each under the nearest heading of a lower level before it.</summary>
	public IReadOnlyList<ManualHeading> Contents()
	{
		var roots = new List<ManualHeading>();
		var open = new Stack<ManualHeading>();
		foreach (ManualHeading heading in _headings)
		{
			while (open.Count > 0 && open.Peek().Level >= heading.Level)
			{
				open.Pop();
			}
			(open.Count > 0 ? open.Peek().Children : roots).Add(heading);
			open.Push(heading);
		}
		return roots;
	}

	/// <summary>The heading a link "#anchor" names, or null.</summary>
	public ManualHeading? Find(string anchor) => _headings.FirstOrDefault(h => h.Anchor == anchor);

	// ------------------------------------------------------------------ reading

	private StackPanel Read(Uri source)
	{
		try
		{
			using Stream stream = AssetLoader.Open(source);
			using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
			reader.MoveToContent();
			return (StackPanel)ReadElement(reader);
		}
		catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException or InvalidCastException or FormatException or ArgumentException)
		{
			Logger.Error(ex, "The document {Source} cannot be read", source);
			var root = new StackPanel { Margin = new Thickness(10) };
			root.Children.Add(new SelectableTextBlock { Text = source.Segments[^1] + " is not built into this build.", TextWrapping = TextWrapping.Wrap });
			return root;
		}
	}

	// the element the reader stands on, with its attributes and the elements inside it
	private object ReadElement(XmlReader reader)
	{
		// without the prefix of the head's own elements (help:FigureBadges)
		string name = reader.LocalName;
		object element = Create(name);
		bool empty = reader.IsEmptyElement;
		if (reader.MoveToFirstAttribute())
		{
			do
			{
				// the root's namespace declarations are XAML's, nothing of the element's
				if (reader.Name == "xmlns" || reader.Prefix == "xmlns")
				{
					continue;
				}
				if (element is FigureBadge badge)
				{
					Apply(badge, reader.Name, reader.Value);
				}
				else
				{
					Apply((AvaloniaObject)element, name, reader.Name, reader.Value);
				}
			}
			while (reader.MoveToNextAttribute());
			reader.MoveToElement();
		}
		if (!empty)
		{
			while (reader.Read() && reader.NodeType != XmlNodeType.EndElement)
			{
				if (reader.NodeType == XmlNodeType.Element)
				{
					Add(element, name, ReadElement(reader));
				}
			}
		}
		return element;
	}

	/// <summary>The control, inline or badge an element names: the kinds the converter writes.</summary>
	private static object Create(string element) => element switch
	{
		"FigureBadges" => new FigureBadges(),
		"Badge" => new FigureBadge(),
		"StackPanel" => new StackPanel(),
		"SelectableTextBlock" => new SelectableTextBlock(),
		"TextBlock" => new TextBlock(),
		"Run" => new Run(),
		"LineBreak" => new LineBreak(),
		"Italic" => new Italic(),
		"Span" => new Span(),
		"InlineUIContainer" => new InlineUIContainer(),
		"Button" => new Button(),
		"Border" => new Border(),
		"Grid" => new Grid(),
		"Viewbox" => new Viewbox(),
		"Canvas" => new Canvas(),
		"Image" => new Image(),
		"Ellipse" => new Ellipse(),
		_ => throw new InvalidDataException($"<{element}> is not an element this reads"),
	};

	/// <summary>
	/// Gives <paramref name="target"/> what the attribute says, each attribute by the type its property has in
	/// Avalonia; numbers are invariant. A text that begins with "{}" is the text behind it: XAML's guard for a value
	/// beginning with a brace.
	/// </summary>
	private void Apply(AvaloniaObject target, string element, string attribute, string value)
	{
		switch (attribute)
		{
			case "Margin":
				((Layoutable)target).Margin = Thickness.Parse(value);
				break;
			case "Padding":
				((TextBlock)target).Padding = Thickness.Parse(value);
				break;
			case "Width":
				((Layoutable)target).Width = Number(value);
				break;
			case "Height":
				((Layoutable)target).Height = Number(value);
				break;
			case "HorizontalAlignment":
				((Layoutable)target).HorizontalAlignment = Enum.Parse<HorizontalAlignment>(value);
				break;
			case "VerticalAlignment":
				((Layoutable)target).VerticalAlignment = Enum.Parse<VerticalAlignment>(value);
				break;
			case "TextWrapping":
				((TextBlock)target).TextWrapping = Enum.Parse<TextWrapping>(value);
				break;
			case "TextAlignment":
				((TextBlock)target).TextAlignment = Enum.Parse<TextAlignment>(value);
				break;
			case "FontSize":
				target.SetValue(TextElement.FontSizeProperty, Number(value));
				break;
			case "FontWeight":
				target.SetValue(TextElement.FontWeightProperty, Enum.Parse<FontWeight>(value));
				break;
			case "FontFamily":
				target.SetValue(TextElement.FontFamilyProperty, Font(value));
				break;
			case "Foreground":
				target.SetValue(TextElement.ForegroundProperty, Paint(value));
				break;
			case "Background":
				((Border)target).Background = Paint(value);
				break;
			case "BorderBrush":
				((Border)target).BorderBrush = Paint(value);
				break;
			case "BorderThickness":
				((Border)target).BorderThickness = Thickness.Parse(value);
				break;
			case "CornerRadius":
				((Border)target).CornerRadius = CornerRadius.Parse(value);
				break;
			case "Fill":
				((Shape)target).Fill = Paint(value);
				break;
			case "Stroke":
				((Shape)target).Stroke = Paint(value);
				break;
			case "StrokeThickness":
				((Shape)target).StrokeThickness = Number(value);
				break;
			case "Text" when target is Run run:
				run.Text = Text(value);
				break;
			case "Text":
				((TextBlock)target).Text = Text(value);
				break;
			case "Tag":
				((Control)target).Tag = Text(value);
				break;
			case "Classes":
				((StyledElement)target).Classes.Add(value);
				break;
			case "ToolTip.Tip":
				ToolTip.SetTip((Control)target, Text(value));
				break;
			case "AutomationProperties.Name":
				AutomationProperties.SetName((Control)target, Text(value));
				break;
			case "Canvas.Left":
				Canvas.SetLeft((Control)target, Number(value));
				break;
			case "Canvas.Top":
				Canvas.SetTop((Control)target, Number(value));
				break;
			case "Grid.Column":
				Grid.SetColumn((Control)target, int.Parse(value, CultureInfo.InvariantCulture));
				break;
			case "ColumnDefinitions":
				((Grid)target).ColumnDefinitions = new ColumnDefinitions(value);
				break;
			case "Stretch" when target is Viewbox box:
				box.Stretch = Enum.Parse<Stretch>(value);
				break;
			case "Stretch":
				((Image)target).Stretch = Enum.Parse<Stretch>(value);
				break;
			case "StretchDirection":
				((Viewbox)target).StretchDirection = Enum.Parse<StretchDirection>(value);
				break;
			case "RenderOptions.BitmapInterpolationMode":
				RenderOptions.SetBitmapInterpolationMode((Visual)target, Enum.Parse<BitmapInterpolationMode>(value));
				break;
			case "Source":
				// a picture of the document, compiled in beside it
				using (Stream picture = AssetLoader.Open(new Uri(value)))
				{
					((Image)target).Source = new Bitmap(picture);
				}
				break;
			case "BaselineAlignment":
				((Inline)target).BaselineAlignment = Enum.Parse<BaselineAlignment>(value);
				break;
			default:
				throw new InvalidDataException($"the <{element}> attribute {attribute} is not one this reads");
		}
	}

	/// <summary>What an attribute says of a figure's badge (<see cref="FigureBadge"/>).</summary>
	private void Apply(FigureBadge badge, string attribute, string value)
	{
		switch (attribute)
		{
			case "X":
				badge.X = Number(value);
				break;
			case "Y":
				badge.Y = Number(value);
				break;
			case "Size":
				badge.Size = Number(value);
				break;
			case "Fill":
				badge.Fill = Paint(value);
				break;
			case "Stroke":
				badge.Stroke = Paint(value);
				break;
			case "StrokeThickness":
				badge.StrokeThickness = Number(value);
				break;
			case "FontFamily":
				badge.FontFamily = Font(value);
				break;
			case "FontSize":
				badge.FontSize = Number(value);
				break;
			case "FontWeight":
				badge.FontWeight = Enum.Parse<FontWeight>(value);
				break;
			case "Foreground":
				badge.Foreground = Paint(value);
				break;
			case "Text":
				badge.Text = Text(value);
				break;
			case "Tip":
				badge.Tip = Text(value);
				break;
			default:
				throw new InvalidDataException($"the <Badge> attribute {attribute} is not one this reads");
		}
	}

	/// <summary>Puts an element into the one it stands in: a block into a panel, an inline into a text, a control into what holds one.</summary>
	private void Add(object parent, string element, object child)
	{
		switch (parent, child)
		{
			case (FigureBadges badges, FigureBadge badge):
				badges.Badges.Add(badge);
				break;
			case (Panel panel, FigureBadges badges):
				// every figure of the document draws with the same numbers
				badges.Numbers = _ink;
				panel.Children.Add(badges);
				break;
			case (Panel panel, Control control):
				panel.Children.Add(control);
				break;
			case (TextBlock text, Inline inline):
				text.Inlines!.Add(inline);
				break;
			case (Span span, Inline inline):
				span.Inlines.Add(inline);
				break;
			case (InlineUIContainer container, Control control):
				container.Child = control;
				break;
			case (Decorator decorator, Control control):
				decorator.Child = control;
				break;
			case (Viewbox box, Control control):
				box.Child = control;
				break;
			case (Button button, Control control):
				button.Content = control;
				break;
			default:
				throw new InvalidDataException($"<{element}> does not hold a {child.GetType().Name}");
		}
	}

	private FontFamily Font(string value)
	{
		if (!_fonts.TryGetValue(value, out FontFamily? font))
		{
			_fonts.Add(value, font = new FontFamily(value));
		}
		return font;
	}

	private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

	private static string Text(string value) => value.StartsWith("{}", StringComparison.Ordinal) ? value[2..] : value;

	private IBrush Paint(string value)
	{
		if (!_brushes.TryGetValue(value, out IBrush? brush))
		{
			_brushes.Add(value, brush = Brush.Parse(value));
		}
		return brush;
	}

	// ------------------------------------------------------------------ headings, figures, links

	// the headings and the figures of a block and of the blocks inside it (a list's rows, a quote), in document order
	private void Gather(Control block)
	{
		switch (block)
		{
			case SelectableTextBlock { Tag: string tag } heading when HeadingTag().Match(tag) is { Success: true } match:
				var text = new StringBuilder();
				AppendText(heading.Inlines, text);
				_headings.Add(new ManualHeading(text.ToString(), int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture), match.Groups[2].Value, heading));
				break;
			case Viewbox:
				_figures++;
				break;
			case Panel panel:
				foreach (Control child in panel.Children)
				{
					Gather(child);
				}
				break;
			case Decorator { Child: { } child }:
				Gather(child);
				break;
		}
	}

	// the text of a text block's inlines as it reads, a line break as a space
	private static void AppendText(InlineCollection? inlines, StringBuilder into)
	{
		if (inlines == null)
		{
			return;
		}
		foreach (Inline inline in inlines)
		{
			switch (inline)
			{
				case Run run:
					into.Append(run.Text);
					break;
				case LineBreak:
					into.Append(' ');
					break;
				case Span span:
					AppendText(span.Inlines, into);
					break;
				case InlineUIContainer { Child: Button { Content: TextBlock link } }:
					AppendText(link.Inlines, into);
					break;
			}
		}
	}

	// a click on one of the document's links, wherever it stands: the button's Tag is the destination, its tooltip the title
	private void OnClick(object? sender, RoutedEventArgs e)
	{
		if (e.Source is Button { Tag: string target } button && button.Classes.Contains("link"))
		{
			e.Handled = true;
			LinkActivated?.Invoke(this, new DocumentLinkEventArgs(target, ToolTip.GetTip(button) as string));
		}
	}
}
