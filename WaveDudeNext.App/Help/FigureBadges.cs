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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace WaveDudeNext.App.Help;

/// <summary>One numbered badge of a figure of the manual: a disc with its number, and what a pointer resting on it says.</summary>
public sealed class FigureBadge
{
	/// <summary>The disc's centre in the figure, in the figure's pixels.</summary>
	public double X { get; set; }

	/// <summary>The disc's centre in the figure, in the figure's pixels.</summary>
	public double Y { get; set; }

	/// <summary>The disc's whole width, its edge line included, in pixels.</summary>
	public double Size { get; set; }

	/// <summary>The disc's colour; null for none.</summary>
	public IBrush? Fill { get; set; }

	/// <summary>The edge line's colour; null for no edge line.</summary>
	public IBrush? Stroke { get; set; }

	/// <summary>The edge line's width in pixels, drawn inside <see cref="Size"/>.</summary>
	public double StrokeThickness { get; set; }

	/// <summary>The number's font; null for the font the figure inherits.</summary>
	public FontFamily? FontFamily { get; set; }

	/// <summary>The number's font size in pixels.</summary>
	public double FontSize { get; set; } = 12;

	/// <summary>The number's weight.</summary>
	public FontWeight FontWeight { get; set; } = FontWeight.Normal;

	/// <summary>The number's colour.</summary>
	public IBrush Foreground { get; set; } = Brushes.Black;

	/// <summary>The number as written.</summary>
	public string Text { get; set; } = "";

	/// <summary>The tooltip, what the number marks; null for none.</summary>
	public string? Tip { get; set; }
}

/// <summary>
/// The numbered badges of a figure of the manual, all drawn by this one control (2026-11-23, the user's choice). As a
/// Border and a text block each, the manual's 811 badges were 1622 controls, every one made, measured, arranged and
/// recorded for drawing by itself, its number laid out twice, wherever in the document it lay: a sixth to a fifth of
/// the manual's layout. Here a badge is a disc and a number drawn in <see cref="Render"/>, the number centred on the
/// disc by its ink, and a number's text is laid out once for the document (<see cref="Numbers"/>), the numbers being
/// 1 to some twenty-five in one font. The tooltip is the control's own, set to the text of the badge the pointer is
/// on. The control is hit only where a disc is drawn, so the picture under it keeps its own tooltip elsewhere. It
/// stands at the figure's corner and reaches as far as its badges do. UI thread.
/// </summary>
public sealed class FigureBadges : Control
{
	private FigureBadge? _hovered;

	/// <summary>The badges, in the order they are drawn; filled before the control is shown.</summary>
	public List<FigureBadge> Badges { get; } = [];

	/// <summary>
	/// The numbers laid out so far and the edge lines' pens, shared by the figures of one document
	/// (<see cref="HelpDocument"/> hands every figure the same one).
	/// </summary>
	public BadgeInk Numbers { get; set; } = new();

	/// <summary>As far right and down as the badges reach.</summary>
	protected override Size MeasureOverride(Size availableSize)
	{
		double right = 0, bottom = 0;
		foreach (FigureBadge badge in Badges)
		{
			right = Math.Max(right, badge.X + badge.Size / 2);
			bottom = Math.Max(bottom, badge.Y + badge.Size / 2);
		}
		return new Size(right, bottom);
	}

	/// <summary>Every badge: the disc, its edge line inside the disc's width, and the number centred on it.</summary>
	public override void Render(DrawingContext context)
	{
		FontFamily inherited = TextElement.GetFontFamily(this);
		foreach (FigureBadge badge in Badges)
		{
			var centre = new Point(badge.X, badge.Y);
			IPen? pen = badge.Stroke != null && badge.StrokeThickness > 0 ? Numbers.Pen(badge.Stroke, badge.StrokeThickness) : null;
			double radius = (badge.Size - (pen == null ? 0 : badge.StrokeThickness)) / 2;
			context.DrawEllipse(badge.Fill, pen, centre, radius, radius);
			if (badge.Text.Length > 0)
			{
				(FormattedText text, Point middle) = Numbers.Number(badge, inherited);
				context.DrawText(text, new Point(badge.X - middle.X, badge.Y - middle.Y));
			}
		}
	}

	/// <summary>The tooltip follows the pointer: the text of the badge it is on, none between the badges.</summary>
	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		Point at = e.GetPosition(this);
		FigureBadge? over = null;
		// the last drawn lies on top
		for (int i = Badges.Count - 1; i >= 0 && over == null; i--)
		{
			FigureBadge badge = Badges[i];
			double dx = at.X - badge.X, dy = at.Y - badge.Y, radius = badge.Size / 2;
			if (dx * dx + dy * dy <= radius * radius)
			{
				over = badge;
			}
		}
		Hover(over);
	}

	/// <inheritdoc />
	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		Hover(null);
	}

	private void Hover(FigureBadge? badge)
	{
		if (!ReferenceEquals(badge, _hovered))
		{
			_hovered = badge;
			ToolTip.SetTip(this, badge?.Tip);
		}
	}
}

/// <summary>
/// What the badges of a document's figures are drawn with, made once and used by all of them: a number's text as
/// laid out, with the middle of its ink, by the number, its font, size, weight and colour; and the edge lines' pens.
/// UI thread.
/// </summary>
public sealed class BadgeInk
{
	private readonly Dictionary<(string Text, FontFamily Family, double Size, FontWeight Weight, IBrush Colour), (FormattedText, Point)> _numbers = [];
	private readonly Dictionary<(IBrush, double), IPen> _pens = [];

	/// <summary>
	/// The badge's number as laid out, and the middle of its ink from the text's corner: the number is centred by what
	/// is drawn of it, not by its line's box, which holds room for descenders a digit does not have.
	/// </summary>
	public (FormattedText Text, Point Middle) Number(FigureBadge badge, FontFamily inherited)
	{
		FontFamily family = badge.FontFamily ?? inherited;
		var key = (badge.Text, family, badge.FontSize, badge.FontWeight, badge.Foreground);
		if (!_numbers.TryGetValue(key, out (FormattedText, Point) number))
		{
			var text = new FormattedText(badge.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
				new Typeface(family, FontStyle.Normal, badge.FontWeight), badge.FontSize, badge.Foreground);
			Rect ink = text.BuildGeometry(default)?.Bounds ?? default;
			// a font without outlines for it: the line's box then
			number = (text, ink.Width > 0 && ink.Height > 0 ? ink.Center : new Point(text.Width / 2, text.Height / 2));
			_numbers.Add(key, number);
		}
		return number;
	}

	/// <summary>The pen of an edge line of this colour and width.</summary>
	public IPen Pen(IBrush stroke, double thickness)
	{
		if (!_pens.TryGetValue((stroke, thickness), out IPen? pen))
		{
			_pens.Add((stroke, thickness), pen = stroke is IImmutableBrush fixedBrush ? new ImmutablePen(fixedBrush, thickness) : new Pen(stroke, thickness));
		}
		return pen;
	}
}
