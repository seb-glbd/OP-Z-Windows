using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OPB.App.Photomatic;

/// <summary>Per-image color settings: the four track 16 color dials plus the held color effects.</summary>
public readonly record struct ColorParams(byte Hue, byte Saturation, byte Brightness, byte Contrast, bool Invert, bool KillRed, bool KillGreen, bool KillBlue, bool Sharpen)
{
    public bool IsIdentity => Math.Abs(Hue - 128) <= 1 && Math.Abs(Saturation - 128) <= 1 && Math.Abs(Brightness - 128) <= 1 && Math.Abs(Contrast - 128) <= 1
        && !Invert && !KillRed && !KillGreen && !KillBlue && !Sharpen;
}

/// <summary>A decoded BGRA32 image held as pixels for CPU processing.</summary>
public sealed class Pixels(byte[] data, int width, int height)
{
    public byte[] Data { get; } = data;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride => Width * 4;

    public static Pixels From(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        var data = new byte[w * h * 4];
        source.CopyPixels(data, w * 4, 0);
        return new Pixels(data, w, h);
    }

    public BitmapSource ToBitmap()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Data, Stride);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>
/// CPU color processing: hue rotation, saturation, brightness, contrast, invert and channel kills are folded
/// into one 3x4 color matrix applied in fixed point over parallel rows; sharpen is a 3x3 kernel pass.
/// </summary>
public static class ImageFx
{
    /// <summary>Rows r, g, b of [mr, mg, mb, offset] in 0..255 units.</summary>
    public static double[] Matrix(ColorParams p)
    {
        double a = (p.Hue - 128) / 128.0 * Math.PI, cos = Math.Cos(a), sin = Math.Sin(a);
        double[] hue =
        [
            0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928, 0,
            0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283, 0,
            0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072, 0,
        ];
        double s = p.Saturation / 128.0;
        s = s <= 1 ? s : 1 + (s - 1) * 2;
        double[] sat =
        [
            0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s, 0,
            0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s, 0,
            0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s, 0,
        ];
        var m = Multiply(sat, hue);
        double b = (p.Brightness - 128) / 128.0 * 160;
        for (int r = 0; r < 3; r++) m[r * 4 + 3] += b;
        double c = Math.Pow(4, (p.Contrast - 128) / 128.0);
        for (int r = 0; r < 3; r++)
        {
            for (int k = 0; k < 4; k++) m[r * 4 + k] *= c;
            m[r * 4 + 3] += 128 * (1 - c);
        }
        if (p.Invert)
            for (int r = 0; r < 3; r++)
            {
                for (int k = 0; k < 4; k++) m[r * 4 + k] = -m[r * 4 + k];
                m[r * 4 + 3] += 255;
            }
        bool[] kill = [p.KillRed, p.KillGreen, p.KillBlue];
        for (int r = 0; r < 3; r++)
            if (kill[r]) for (int k = 0; k < 4; k++) m[r * 4 + k] = 0;
        return m;
    }

    /// <summary>a ∘ b: applies b first, then a.</summary>
    static double[] Multiply(double[] a, double[] b)
    {
        var m = new double[12];
        for (int r = 0; r < 3; r++)
        {
            for (int k = 0; k < 3; k++)
                m[r * 4 + k] = a[r * 4] * b[k] + a[r * 4 + 1] * b[4 + k] + a[r * 4 + 2] * b[8 + k];
            m[r * 4 + 3] = a[r * 4] * b[3] + a[r * 4 + 1] * b[7] + a[r * 4 + 2] * b[11] + a[r * 4 + 3];
        }
        return m;
    }

    public static Pixels Apply(Pixels src, ColorParams p)
    {
        var dst = new byte[src.Data.Length];
        Apply(src.Data, dst, src.Width, src.Height, p);
        return new Pixels(dst, src.Width, src.Height);
    }

    public static void Apply(byte[] src, byte[] dst, int w, int h, ColorParams p)
    {
        var md = Matrix(p);
        var m = new int[12];
        for (int i = 0; i < 12; i++) m[i] = (int)Math.Round(md[i] * 1024);
        Parallel.For(0, h, y =>
        {
            int i = y * w * 4, end = i + w * 4;
            for (; i < end; i += 4)
            {
                int b = src[i], g = src[i + 1], r = src[i + 2];
                int nr = (m[0] * r + m[1] * g + m[2] * b + m[3]) >> 10;
                int ng = (m[4] * r + m[5] * g + m[6] * b + m[7]) >> 10;
                int nb = (m[8] * r + m[9] * g + m[10] * b + m[11]) >> 10;
                dst[i] = (byte)(nb < 0 ? 0 : nb > 255 ? 255 : nb);
                dst[i + 1] = (byte)(ng < 0 ? 0 : ng > 255 ? 255 : ng);
                dst[i + 2] = (byte)(nr < 0 ? 0 : nr > 255 ? 255 : nr);
                dst[i + 3] = src[i + 3];
            }
        });
        if (p.Sharpen) Sharpen(dst, w, h);
    }

    static void Sharpen(byte[] px, int w, int h)
    {
        var src = (byte[])px.Clone();
        int stride = w * 4;
        Parallel.For(1, h - 1, y =>
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * stride + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int v = 5 * src[i + c] - src[i + c - 4] - src[i + c + 4] - src[i + c - stride] - src[i + c + stride];
                    px[i + c] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
            }
        });
    }

    /// <summary>Decodes an image file, applying EXIF orientation and limiting the long side.</summary>
    public static BitmapSource Load(string path, int maxSide)
    {
        using var stream = System.IO.File.OpenRead(path);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource bmp = frame;
        int longSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longSide > maxSide)
        {
            double k = (double)maxSide / longSide;
            bmp = new TransformedBitmap(bmp, new ScaleTransform(k, k));
        }
        int orientation = 1;
        try { if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort o) orientation = o; }
        catch (Exception) { /* no EXIF */ }
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1), 3 => new RotateTransform(180), 4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90), 7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270), _ => null,
        };
        if (t is not null) bmp = new TransformedBitmap(bmp, t);
        var result = new WriteableBitmap(bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0));
        result.Freeze();
        return result;
    }

    /// <summary>Decodes a GIF into fully composited frames with their delays.</summary>
    public static List<(BitmapSource Frame, TimeSpan Delay)> LoadGif(string path, int maxSide)
    {
        using var stream = System.IO.File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int w = decoder.Frames[0].PixelWidth, h = decoder.Frames[0].PixelHeight;
        try
        {
            if (decoder.Metadata?.GetQuery("/logscrdesc/Width") is ushort lw && decoder.Metadata.GetQuery("/logscrdesc/Height") is ushort lh) { w = lw; h = lh; }
        }
        catch (Exception) { }
        double k = Math.Min(1, (double)maxSide / Math.Max(w, h));
        var frames = new List<(BitmapSource, TimeSpan)>();
        BitmapSource? previous = null;
        foreach (var f in decoder.Frames.Take(300))
        {
            int left = 0, top = 0, delay = 10, disposal = 0;
            if (f.Metadata is BitmapMetadata md)
            {
                try
                {
                    if (md.GetQuery("/imgdesc/Left") is ushort l) left = l;
                    if (md.GetQuery("/imgdesc/Top") is ushort t) top = t;
                    if (md.GetQuery("/grctlext/Delay") is ushort d) delay = d;
                    if (md.GetQuery("/grctlext/Disposal") is byte dp) disposal = dp;
                }
                catch (Exception) { }
            }
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (previous is not null) dc.DrawImage(previous, new Rect(0, 0, w, h));
                dc.DrawImage(f, new Rect(left, top, f.PixelWidth, f.PixelHeight));
            }
            var full = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            full.Render(visual);
            full.Freeze();
            BitmapSource output = full;
            if (k < 1)
            {
                var scaled = new WriteableBitmap(new TransformedBitmap(full, new ScaleTransform(k, k)));
                scaled.Freeze();
                output = scaled;
            }
            frames.Add((output, TimeSpan.FromMilliseconds(Math.Max(20, delay * 10))));
            // Keep the composite unless the frame asks to restore the background.
            previous = disposal == 2 ? null : full;
        }
        return frames;
    }
}
