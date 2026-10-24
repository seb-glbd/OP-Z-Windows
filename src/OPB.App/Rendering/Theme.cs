using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OPB.App.Rendering;

/// <summary>Colors and type of the OP-Z app (sampled from the official app's screenshots).</summary>
public static class Theme
{
    public static readonly Color BackgroundColor = Color.FromRgb(0xD0, 0xD0, 0xD0);
    public static readonly Color RuleColor = Color.FromRgb(0xB3, 0xB3, 0xB3);
    public static readonly Color MidColor = Color.FromRgb(0x80, 0x80, 0x80);
    public static readonly Color InkColor = Color.FromRgb(0x13, 0x13, 0x13);
    public static readonly Color RedColor = Color.FromRgb(0xD1, 0x1D, 0x32);
    public static readonly Color YellowColor = Color.FromRgb(0xEB, 0xB0, 0x00);
    public static readonly Color GreenColor = Color.FromRgb(0x02, 0x5A, 0x30);
    public static readonly Color BlueColor = Color.FromRgb(0x17, 0x5E, 0x83);
    public static readonly Color TapeBlueColor = Color.FromRgb(0x13, 0x6A, 0xA3);

    public static readonly Brush Background = Freeze(new SolidColorBrush(BackgroundColor));
    public static readonly Brush Rule = Freeze(new SolidColorBrush(RuleColor));
    public static readonly Brush Mid = Freeze(new SolidColorBrush(MidColor));
    public static readonly Brush Ink = Freeze(new SolidColorBrush(InkColor));
    public static readonly Brush Red = Freeze(new SolidColorBrush(RedColor));
    public static readonly Brush Yellow = Freeze(new SolidColorBrush(YellowColor));
    public static readonly Brush Green = Freeze(new SolidColorBrush(GreenColor));
    public static readonly Brush Blue = Freeze(new SolidColorBrush(BlueColor));
    public static readonly Brush White = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)));
    public static readonly Brush StatusBar = Ink;
    public static readonly Brush Hatch = MakeHatch(RuleColor, BackgroundColor);
    public static readonly Brush DarkHatch = MakeHatch(Color.FromRgb(0x2A, 0x2A, 0x2A), InkColor);

    /// <summary>The four encoder colors in dial order: green, blue, yellow, red.</summary>
    public static readonly Brush[] Dials = [Green, Blue, Yellow, Red];

    /// <summary>Frame color of each parameter page (device LED: white, green, purple, orange).</summary>
    public static readonly Brush[] Pages = [Ink, Green, Blue, Yellow];

    public static readonly Pen RulePen = Freeze(new Pen(Rule, 2));
    public static readonly Pen ThickRulePen = Freeze(new Pen(Mid, 2));

    public static readonly FontFamily Font = new("Bahnschrift");
    public static readonly Typeface Light = new(Font, FontStyles.Normal, FontWeights.Light, FontStretches.SemiCondensed);
    public static readonly Typeface Regular = new(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.SemiCondensed);
    public static readonly Typeface Bold = new(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.SemiCondensed);

    public static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    static Brush MakeHatch(Color line, Color back)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(back), null, new Rect(0, 0, 14, 14));
            var pen = new Pen(new SolidColorBrush(line), 1.6);
            dc.DrawLine(pen, new Point(-1, 15), new Point(15, -1));
            dc.DrawLine(pen, new Point(-8, 8), new Point(8, -8));
            dc.DrawLine(pen, new Point(6, 22), new Point(22, 6));
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 14, 14),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 14, 14),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}

public enum Align { Left, Center, Right }

/// <summary>Drawing helpers shared by every screen. Coordinates are in design units.</summary>
public static class Draw
{
    static readonly Dictionary<(string, Typeface, double, Brush), FormattedText> Cache = new();
    public static double PixelsPerDip { get; set; } = 1;

    public static FormattedText Text(string text, double size, Brush brush, Typeface? face = null)
    {
        face ??= Theme.Light;
        var key = (text, face, size, brush);
        if (Cache.TryGetValue(key, out var ft)) return ft;
        if (Cache.Count > 4000) Cache.Clear();
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, PixelsPerDip);
        Cache[key] = ft;
        return ft;
    }

    /// <summary>Draws text with its vertical center at <paramref name="y"/>.</summary>
    public static Size Label(DrawingContext dc, string text, double x, double y, double size, Brush brush, Align align = Align.Left, Typeface? face = null)
    {
        var ft = Text(text, size, brush, face);
        double left = align switch { Align.Center => x - ft.Width / 2, Align.Right => x - ft.Width, _ => x };
        // Bahnschrift caps sit slightly above the em-box center; nudge so caps look centered.
        dc.DrawText(ft, new Point(left, y - ft.Height / 2 + size * 0.04));
        return new Size(ft.Width, ft.Height);
    }

    public static void HLine(DrawingContext dc, double x0, double x1, double y, Pen? pen = null) =>
        dc.DrawLine(pen ?? Theme.RulePen, new Point(x0, y), new Point(x1, y));

    public static void VLine(DrawingContext dc, double x, double y0, double y1, Pen? pen = null) =>
        dc.DrawLine(pen ?? Theme.RulePen, new Point(x, y0), new Point(x, y1));

    public static void Frame(DrawingContext dc, Rect rect, Brush brush, double thickness = 4)
    {
        var inset = new Rect(rect.X + thickness / 2, rect.Y + thickness / 2, Math.Max(0, rect.Width - thickness), Math.Max(0, rect.Height - thickness));
        dc.DrawRectangle(null, new Pen(brush, thickness), inset);
    }

    /// <summary>Knob angle in degrees clockwise from 12 o'clock for a 0..1 value (270° sweep).</summary>
    public static double KnobAngle(double value01) => -135 + 270 * Math.Clamp(value01, 0, 1);

    /// <summary>
    /// The app's round parameter indicator: a filled disc with a screw-head cross and an
    /// outward stem, both rotated to the value.
    /// </summary>
    public static void Knob(DrawingContext dc, Point center, double value01, Brush fill, double radius = 22, bool stem = true)
    {
        double angle = KnobAngle(value01) * Math.PI / 180;
        var dir = new Vector(Math.Sin(angle), -Math.Cos(angle));
        if (stem)
        {
            var stemPen = new Pen(fill, 3.5);
            dc.DrawLine(stemPen, center + dir * radius * 0.9, center + dir * radius * 1.75);
        }
        dc.DrawEllipse(fill, null, center, radius, radius);
        Cross(dc, center, radius * 0.5, angle);
    }

    public static void Cross(DrawingContext dc, Point center, double half, double angleRad = 0)
    {
        var pen = new Pen(Theme.White, half * 0.42) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        var a = new Vector(Math.Cos(angleRad), Math.Sin(angleRad)) * half;
        var b = new Vector(-Math.Sin(angleRad), Math.Cos(angleRad)) * half;
        dc.DrawLine(pen, center - a, center + a);
        dc.DrawLine(pen, center - b, center + b);
    }

    /// <summary>A small solid disc with a plus, used as an envelope handle.</summary>
    public static void Handle(DrawingContext dc, Point center, Brush fill, double radius = 16) =>
        Knob(dc, center, 0.5, fill, radius, stem: false);

    public static Point P(double x, double y) => new(x, y);
}
