using System.Windows;
using System.Windows.Media;

namespace OPB.App.Rendering;

/// <summary>Buttons, toggles and text helpers for the settings-style screens (devices, MIDI setup, configurator).</summary>
public static class Widgets
{
    /// <summary>A flat framed button with a centered caption.</summary>
    public static void Button(DrawingContext dc, Rect r, string label, bool enabled = true, bool primary = false, double size = 30)
    {
        if (primary && enabled) dc.DrawRectangle(Theme.Ink, null, r);
        else Draw.Frame(dc, r, enabled ? Theme.Ink : Theme.Rule, 3);
        Draw.Label(dc, label, r.X + r.Width / 2, r.Y + r.Height / 2, size, primary && enabled ? Theme.White : enabled ? Theme.Ink : Theme.Mid, Align.Center, Theme.Regular);
    }

    /// <summary>A square on/off box: solid with a cross when on, an outline when off.</summary>
    public static void Toggle(DrawingContext dc, Point center, bool on, bool enabled = true, double size = 28)
    {
        var r = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
        if (on)
        {
            dc.DrawRectangle(enabled ? Theme.Ink : Theme.Mid, null, r);
            Draw.Cross(dc, center, size * 0.3, Math.PI / 4);
        }
        else Draw.Frame(dc, r, enabled ? Theme.Mid : Theme.Rule, 3);
    }

    /// <summary>A small round +/- button.</summary>
    public static void Stepper(DrawingContext dc, Point center, bool plus, bool enabled = true, double radius = 15)
    {
        if (enabled) dc.DrawEllipse(Theme.Mid, null, center, radius, radius);
        else dc.DrawEllipse(null, new Pen(Theme.Mid, 2), center, radius - 1, radius - 1);
        var pen = new Pen(enabled ? Theme.White : Theme.Mid, radius * 0.24);
        dc.DrawLine(pen, new Point(center.X - radius * 0.5, center.Y), new Point(center.X + radius * 0.5, center.Y));
        if (plus) dc.DrawLine(pen, new Point(center.X, center.Y - radius * 0.5), new Point(center.X, center.Y + radius * 0.5));
    }

    /// <summary>Trims text with an ellipsis until it fits <paramref name="width"/>.</summary>
    public static string Fit(string text, double width, double size, Typeface? face = null)
    {
        if (Draw.Text(text, size, Theme.Ink, face).Width <= width) return text;
        while (text.Length > 1 && Draw.Text(text + "…", size, Theme.Ink, face).Width > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    /// <summary>Greedy word wrap to <paramref name="width"/>.</summary>
    public static List<string> Wrap(string text, double width, double size, Typeface? face = null)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Draw.Text(candidate, size, Theme.Ink, face).Width > width)
            {
                lines.Add(line);
                line = word;
            }
            else line = candidate;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>Draws wrapped lines starting with the first line's center at <paramref name="y"/>; returns the y after the block.</summary>
    public static double Paragraph(DrawingContext dc, string text, double x, double y, double width, double size, Brush brush, Align align = Align.Left, Typeface? face = null, double leading = 1.3)
    {
        foreach (var line in Wrap(text, width, size, face))
        {
            Draw.Label(dc, line, x, y, size, brush, align, face);
            y += size * leading;
        }
        return y;
    }
}
