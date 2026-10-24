using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace OPB.App.Motion;

/// <summary>The paks built into OP-B (official videopaks are Unity asset bundles and can't be played here).</summary>
public static class BuiltInPaks
{
    public static IReadOnlyList<MotionPak> Create() => [new CubesPak(), new TunnelPak(), new SparksPak(), new TerrainPak()];
}

/// <summary>3D blocks circling a core (Viewport3D): kick pulses every block, synth notes bump single ones, snare flashes.</summary>
public sealed class CubesPak : MotionPak
{
    const int Max = 72;
    readonly Viewport3D _view = new() { ClipToBounds = true, IsHitTestVisible = false };
    readonly PerspectiveCamera _camera = new() { FieldOfView = 55, NearPlaneDistance = 0.1, FarPlaneDistance = 200 };
    readonly GeometryModel3D[] _cubes = new GeometryModel3D[Max];
    readonly SolidColorBrush[] _fill = new SolidColorBrush[Max], _glow = new SolidColorBrush[Max];
    readonly double[] _bump = new double[Max];
    readonly GeometryModel3D _core;
    readonly SolidColorBrush _coreFill = new(), _coreGlow = new();
    readonly AmbientLight _ambient = new(Color.FromRgb(60, 60, 60));
    readonly PointLight _flash = new(Colors.Black, new Point3D(0, 2.5, 0)) { Range = 30 };

    public CubesPak()
    {
        var mesh = Cube();
        var group = new Model3DGroup();
        group.Children.Add(_ambient);
        group.Children.Add(new DirectionalLight(Color.FromRgb(230, 230, 230), new Vector3D(-0.5, -1, -0.7)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(70, 70, 90), new Vector3D(0.6, 0.4, 0.8)));
        group.Children.Add(_flash);
        for (int i = 0; i < Max; i++)
        {
            _fill[i] = new SolidColorBrush();
            _glow[i] = new SolidColorBrush();
            _cubes[i] = new GeometryModel3D(mesh, new MaterialGroup { Children = { new DiffuseMaterial(_fill[i]), new EmissiveMaterial(_glow[i]) } });
            group.Children.Add(_cubes[i]);
        }
        _core = new GeometryModel3D(mesh, new MaterialGroup { Children = { new DiffuseMaterial(_coreFill), new EmissiveMaterial(_coreGlow) } });
        group.Children.Add(_core);
        _view.Children.Add(new ModelVisual3D { Content = group });
        _view.Camera = _camera;
    }

    public override string Name => "CUBES";
    public override string Description => "3D BLOCKS ORBIT A CORE AND PULSE WITH THE DRUMS";
    public override string[] Cameras => ["FRONT", "ORBIT", "TOP", "LOW", "CLOSE"];
    public override FrameworkElement Element => _view;

    public override void Update(MotionContext c)
    {
        int n = 8 + (int)(c.Density * (Max - 8));
        foreach (var (track, note, _) in c.Notes)
            if (track >= 4) _bump[(note * 7 + track * 3) % n] = 1;
        double size = 0.15 + c.Size * 0.6;
        for (int i = 0; i < Max; i++)
        {
            if (i >= n) { _cubes[i].Transform = new ScaleTransform3D(0, 0, 0); continue; }
            double a = i / (double)n * Math.PI * 2 + c.Time * 0.35;
            double ring = 4.2 + Math.Sin(i * 1.7 + c.Time * 0.6) * 1.1 + c.Bass * 0.8;
            var pos = new Vector3D(Math.Cos(a) * ring, Math.Sin(a * 3 + c.Time) * 1.1 + Math.Sin(i * 2.3) * 0.6, Math.Sin(a) * ring);
            double s = size * (0.6 + 0.4 * Math.Sin(i * 3.1) * Math.Sin(i * 3.1)) * (1 + c.Kick * 0.45 + _bump[i] * 0.7);
            var m = Matrix3D.Identity;
            m.Scale(new Vector3D(s, s, s));
            m.Rotate(new Quaternion(new Vector3D(Math.Sin(i), 1, Math.Cos(i)), c.Time * 70 + i * 23));
            m.Translate(pos);
            _cubes[i].Transform = new MatrixTransform3D(m);
            _fill[i].Color = c.Color(i * 140.0 / n, 0.7, 0.95);
            _glow[i].Color = c.Glow(i * 140.0 / n, 0.5, Math.Min(1, c.Snare * 0.5 + _bump[i] * 0.9));
            _bump[i] *= Math.Exp(-c.Dt * 5);
        }
        var core = Matrix3D.Identity;
        double cs = 1.1 + c.Bass * 0.6 + c.Kick * 0.3;
        core.Scale(new Vector3D(cs, cs, cs));
        core.Rotate(new Quaternion(new Vector3D(1, 1, 0), c.Time * 40));
        _core.Transform = new MatrixTransform3D(core);
        _coreFill.Color = c.Color(200, 0.08, 0.92);
        _coreGlow.Color = c.Glow(200, 0.6, c.Synth * 0.7);
        _flash.Color = c.Glow(30, 0.4, c.Snare);
        _ambient.Color = c.Invert ? Color.FromRgb(140, 140, 140) : Color.FromRgb(60, 60, 60);

        double t = c.Time;
        (Point3D eye, Point3D target) = c.Camera switch
        {
            1 => (new Point3D(Math.Cos(t * 0.4) * 10, 3 + Math.Sin(t * 0.3) * 2, Math.Sin(t * 0.4) * 10), new Point3D(0, 0, 0)),
            2 => (new Point3D(0, 13, 0.01), new Point3D(0, 0, 0)),
            3 => (new Point3D(7, -2.6, 6), new Point3D(0, 0.8, 0)),
            4 => (new Point3D(Math.Sin(t * 0.5) * 1.5, 0.6, 5.2), new Point3D(0, 0, 0)),
            _ => (new Point3D(0, 1.6, 11), new Point3D(0, 0, 0)),
        };
        _camera.Position = eye;
        _camera.LookDirection = target - eye;
        _camera.UpDirection = c.Camera == 2 ? new Vector3D(0, 0, -1) : new Vector3D(0, 1, 0);
    }

    static MeshGeometry3D Cube()
    {
        var mesh = new MeshGeometry3D();
        Vector3D[] normals = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        foreach (var nrm in normals)
        {
            var u = Math.Abs(nrm.Y) > 0.5 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
            var v = Vector3D.CrossProduct(nrm, u);
            int b = mesh.Positions.Count;
            foreach (var (su, sv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                var p = nrm * 0.5 + u * (su * 0.5) + v * (sv * 0.5);
                mesh.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                mesh.Normals.Add(nrm);
            }
            // Wind each face counter-clockwise seen from outside.
            bool flip = Vector3D.DotProduct(Vector3D.CrossProduct(u, v), nrm) < 0;
            int[] tri = flip ? [0, 2, 1, 0, 3, 2] : [0, 1, 2, 0, 2, 3];
            foreach (int k in tri) mesh.TriangleIndices.Add(b + k);
        }
        mesh.Freeze();
        return mesh;
    }
}

/// <summary>A wireframe tunnel of twisting polygon rings flying at the camera; the kick widens it, notes light spokes.</summary>
public sealed class TunnelPak : MotionPak
{
    const double Spacing = 1.4;
    readonly double[] _spoke = new double[16];
    double _travel;

    public override string Name => "TUNNEL";
    public override string Description => "WIREFRAME RINGS RUSH PAST, TWISTING WITH THE MUSIC";
    public override string[] Cameras => ["INSIDE", "SIDE", "ABOVE", "SPIN", "FAR"];

    public override void Update(MotionContext c)
    {
        _travel += c.Dt * (3 + c.Speed * 14 + c.Kick * 6);
        foreach (var (track, note, _) in c.Notes)
            if (track >= 4) _spoke[note % _spoke.Length] = 1;
        for (int i = 0; i < _spoke.Length; i++) _spoke[i] *= Math.Exp(-c.Dt * 4);
    }

    public override void Render(DrawingContext dc, Size size, MotionContext c)
    {
        int rings = 12 + (int)(c.Density * 34), sides = 4 + (int)(c.Density * 8);
        double t = c.Time;
        var proj = c.Camera switch
        {
            1 => new Projector(new Point3D(8, 3, 2), new Point3D(0, 0, 14), 0, 60, size),
            2 => new Projector(new Point3D(0, 16, 6), new Point3D(0, 0, 18), 0, 60, size),
            3 => new Projector(new Point3D(0, 0, -1.5), new Point3D(0, 0, 10), t * 1.4, 75, size),
            4 => new Projector(new Point3D(0, 0, -22), new Point3D(0, 0, 10), 0, 40, size),
            _ => new Projector(new Point3D(Math.Sin(t * 0.7) * 0.5, Math.Cos(t * 0.5) * 0.4, -1.5), new Point3D(0, 0, 10), Math.Sin(t * 0.3) * 0.25, 75, size),
        };
        double offset = _travel % Spacing;
        var pts = new Point[rings, sides];
        var ok = new bool[rings, sides];
        var depths = new double[rings];
        for (int r = 0; r < rings; r++)
        {
            double z = r * Spacing - offset;
            double radius = 2.2 + c.Kick * 0.9 * Math.Exp(-r * 0.15) + c.Bass * 0.5 * Math.Sin(r * 0.6 + t * 2);
            double twist = z * 0.09 + t * 0.5;
            for (int k = 0; k < sides; k++)
            {
                double a = twist + k * Math.PI * 2 / sides;
                ok[r, k] = proj.Project(new Point3D(Math.Cos(a) * radius, Math.Sin(a) * radius, z), out pts[r, k], out depths[r]);
            }
        }
        double baseWidth = 1.2 + c.Size * 7;
        for (int r = rings - 1; r >= 0; r--)
        {
            double fade = Math.Clamp(1 - r / (double)rings, 0, 1);
            double width = baseWidth * Math.Clamp(proj.Scale(Math.Max(depths[r], 1)) / proj.Scale(6), 0.15, 3);
            var color = c.Color(r * 9 + c.Snare * 120, 0.75 - c.Snare * 0.5, 0.25 + 0.75 * fade);
            var pen = new Pen(new SolidColorBrush(color), width) { LineJoin = PenLineJoin.Round };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                bool started = false;
                for (int k = 0; k <= sides; k++)
                {
                    int i = k % sides;
                    if (!ok[r, i]) { started = false; continue; }
                    if (!started) { ctx.BeginFigure(pts[r, i], false, false); started = true; }
                    else ctx.LineTo(pts[r, i], true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            if (r + 1 >= rings) continue;
            for (int k = 0; k < sides; k++)
            {
                if (!ok[r, k] || !ok[r + 1, k]) continue;
                double lit = _spoke[k % _spoke.Length];
                var spokePen = new Pen(new SolidColorBrush(c.Color(k * 30 + 180, 0.8, (0.18 + lit * 0.8 + c.Hat * 0.3) * fade)), Math.Max(0.8, width * (0.35 + lit * 0.6)));
                dc.DrawLine(spokePen, pts[r, k], pts[r + 1, k]);
            }
        }
    }
}

/// <summary>A particle field: a slow ambient stream, bursts colored per track on every note, a shockwave on the kick.</summary>
public sealed class SparksPak : MotionPak
{
    struct Particle { public double X, Y, Z, Vx, Vy, Vz, Life, Decay, Hue, Size; }

    const int Max = 3000;
    readonly Particle[] _p = new Particle[Max];
    readonly Random _random = new(7);
    readonly Dictionary<int, Brush> _brushes = new();
    int _count;
    double _emit;

    public override string Name => "SPARKS";
    public override string Description => "PARTICLES BURST FROM EVERY NOTE, COLORED BY TRACK";
    public override string[] Cameras => ["FRONT", "SIDE", "TOP", "ORBIT", "INSIDE"];

    double R(double a = 1) => (_random.NextDouble() * 2 - 1) * a;

    void Spawn(double x, double y, double z, double speed, double hue, double size, double decay)
    {
        if (_count >= Max) return;
        var d = new Vector3D(R(), R(), R());
        if (d.Length < 0.01) d = new Vector3D(0, 1, 0);
        d.Normalize();
        _p[_count++] = new Particle { X = x, Y = y, Z = z, Vx = d.X * speed, Vy = d.Y * speed, Vz = d.Z * speed, Life = 1, Decay = decay, Hue = hue, Size = size };
    }

    public override void Update(MotionContext c)
    {
        double dt = c.Dt;
        _emit += dt * (15 + c.Density * 420);
        double t = c.Time;
        for (; _emit >= 1; _emit--)
        {
            double a = _random.NextDouble() * Math.PI * 2;
            Spawn(Math.Cos(a) * 4, Math.Sin(t * 0.7 + a * 2) * 0.6, Math.Sin(a) * 4, 0.4 + c.Speed * 1.5, a * 30, 0.6, 0.25 + _random.NextDouble() * 0.2);
        }
        foreach (var (track, note, velocity) in c.Notes)
        {
            int count = 12 + velocity / 3 + (int)(c.Density * 40);
            double hue = track * 40 + note % 12 * 4;
            (double x, double y, double z, double speed) = track switch
            {
                0 => (0.0, 0.0, 0.0, 7.0),
                1 => (R(1.5), 1.5, R(1.5), 5.0),
                4 => (0.0, -2.0, 0.0, 3.0),
                _ => ((note - 60) * 0.18, R(1.5), R(1), 3.5),
            };
            for (int i = 0; i < count; i++) Spawn(x, y, z, speed * (0.4 + _random.NextDouble() * 0.8), hue + R(12), track == 0 ? 1.4 : 1, 0.5 + _random.NextDouble() * 0.6);
        }
        double damping = Math.Exp(-dt * 0.9);
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _p[i];
            if (c.Kick > 0.3)
            {
                double len = Math.Max(0.5, Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z));
                p.Vx += p.X / len * c.Kick * dt * 18;
                p.Vy += p.Y / len * c.Kick * dt * 18;
                p.Vz += p.Z / len * c.Kick * dt * 18;
            }
            // A gentle swirl around the vertical axis.
            p.Vx += -p.Z * dt * 0.35 * (0.3 + c.Speed);
            p.Vz += p.X * dt * 0.35 * (0.3 + c.Speed);
            p.Vx *= damping; p.Vy *= damping; p.Vz *= damping;
            p.X += p.Vx * dt; p.Y += p.Vy * dt; p.Z += p.Vz * dt;
            p.Life -= p.Decay * dt;
            if (p.Life <= 0) { _p[i--] = _p[--_count]; }
        }
    }

    public override void Render(DrawingContext dc, Size size, MotionContext c)
    {
        double t = c.Time;
        var proj = c.Camera switch
        {
            1 => new Projector(new Point3D(15, 2, 0), new Point3D(0, 0, 0), 0, 55, size),
            2 => new Projector(new Point3D(0, 17, 0.1), new Point3D(0, 0, 0), 0, 55, size),
            3 => new Projector(new Point3D(Math.Cos(t * 0.3) * 13, 4 + Math.Sin(t * 0.2) * 3, Math.Sin(t * 0.3) * 13), new Point3D(0, 0, 0), 0, 55, size),
            4 => new Projector(new Point3D(0, 0.3, -3.2), new Point3D(0, 0, 4), Math.Sin(t * 0.4) * 0.2, 80, size),
            _ => new Projector(new Point3D(0, 1, -15), new Point3D(0, 0, 0), 0, 50, size),
        };
        if (_brushes.Count > 3000) _brushes.Clear();
        double baseSize = 0.03 + c.Size * 0.12;
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _p[i];
            if (!proj.Project(new Point3D(p.X, p.Y, p.Z), out var s, out double depth)) continue;
            double radius = Math.Clamp(baseSize * p.Size * proj.Scale(depth), 0.6, 60);
            int hueBucket = (int)(((p.Hue % 360) + 360) % 360 / 10), alpha = (int)Math.Clamp(p.Life * 8, 1, 8);
            int key = hueBucket | alpha << 6 | (c.Invert ? 1 << 10 : 0) | (int)(((c.Hue * 360 + c.HueShift) % 360 + 360) % 360 / 10) << 11;
            if (!_brushes.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(c.Color(hueBucket * 10, 0.65, 1, alpha / 8.0));
                brush.Freeze();
                _brushes[key] = brush;
            }
            dc.DrawEllipse(brush, null, s, radius, radius);
        }
    }
}

/// <summary>Ridge lines of a scrolling landscape (painter's order, so near ridges hide far ones); bass raises it, kicks ripple it.</summary>
public sealed class TerrainPak : MotionPak
{
    const int Columns = 56;
    double _travel, _ripple = 99;

    public override string Name => "TERRAIN";
    public override string Description => "A SCROLLING LANDSCAPE OF RIDGE LINES THAT RISES WITH THE BASS";
    public override string[] Cameras => ["FLY", "LOW", "HIGH", "SIDE", "BANK"];

    public override void Update(MotionContext c)
    {
        _travel += c.Dt * (1.5 + c.Speed * 9);
        if (c.Notes.Any(n => n.Track == 0)) _ripple = 0;
        _ripple += c.Dt * 14;
    }

    double Height(double x, double z, double near, MotionContext c)
    {
        double h = Math.Sin(x * 0.55 + z * 0.31) * 0.6 + Math.Sin(x * 1.6 - z * 0.83 + c.Time * 0.7) * 0.25 + Math.Sin(z * 0.47 + x * 0.21) * 0.7;
        double valley = Math.Min(1, Math.Abs(x) / 5);
        double r = Math.Sqrt(x * x + (near - 8) * (near - 8));
        double ripple = Math.Exp(-Math.Pow(r - _ripple, 2) * 0.4) * 1.2 * Math.Exp(-_ripple * 0.08);
        return (h * (0.5 + c.Size * 1.8) + c.Bass * 1.4 * Math.Sin(x * 0.8 + c.Time)) * valley + ripple;
    }

    public override void Render(DrawingContext dc, Size size, MotionContext c)
    {
        int rows = 18 + (int)(c.Density * 42);
        double t = c.Time, depth = 30, step = depth / rows;
        var proj = c.Camera switch
        {
            1 => new Projector(new Point3D(0, 0.9, -3), new Point3D(0, 0.3, 12), 0, 70, size),
            2 => new Projector(new Point3D(0, 11, -3), new Point3D(0, 0, 13), 0, 60, size),
            3 => new Projector(new Point3D(16, 5, 10), new Point3D(0, 0, 12), 0, 55, size),
            4 => new Projector(new Point3D(Math.Sin(t * 0.4) * 3, 3, -5), new Point3D(0, 0, 14), Math.Sin(t * 0.4) * 0.35, 65, size),
            _ => new Projector(new Point3D(0, 3.2, -6), new Point3D(0, 0, 12), 0, 65, size),
        };
        var fill = new SolidColorBrush(c.Background);
        fill.Freeze();
        double offset = _travel % step;
        for (int r = rows - 1; r >= 0; r--)
        {
            double z = r * step - offset;
            double worldZ = z + _travel;
            var line = new List<Point>(Columns);
            double ridgeDepth = 1;
            for (int k = 0; k < Columns; k++)
            {
                double x = (k / (double)(Columns - 1) - 0.5) * 30;
                if (proj.Project(new Point3D(x, Height(x, worldZ, z, c), z), out var p, out ridgeDepth)) line.Add(p);
            }
            if (line.Count < 2) continue;
            double fade = Math.Clamp(1 - r / (double)rows, 0, 1);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(line[0].X, size.Height * 4), true, true);
                foreach (var p in line) ctx.LineTo(p, true, true);
                ctx.LineTo(new Point(line[^1].X, size.Height * 4), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, null, g);
            var ridge = new StreamGeometry();
            using (var ctx = ridge.Open())
            {
                ctx.BeginFigure(line[0], false, false);
                for (int i = 1; i < line.Count; i++) ctx.LineTo(line[i], true, true);
            }
            ridge.Freeze();
            double width = Math.Clamp((1 + c.Size * 3) * proj.Scale(ridgeDepth) / proj.Scale(8), 0.6, 6);
            var color = c.Color(worldZ * 4 + c.Synth * 90, 0.55 + c.Snare * 0.3, 0.2 + 0.8 * fade + c.Hat * 0.2);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), width) { LineJoin = PenLineJoin.Round }, ridge);
        }
    }
}
