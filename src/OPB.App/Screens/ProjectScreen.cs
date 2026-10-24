using System.Windows;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>
/// Project (hold PROJECT): the 16 patterns of the active project, the active chain with its
/// playing position, and the 15 saved chains. Tap a pattern to recall it; hold one and add
/// more (other fingers, or drag across) to build a chain that is recalled on release.
/// </summary>
[Screen(ScreenKeys.Project)]
public sealed class ProjectScreen : ScreenView
{
    const double TitleY = 40, Row0 = 82, Row1 = 212, Row2 = 342, Row3 = 472, ColLabel = 216, ColW = 130, GridEnd = ColLabel + 8 * ColW;
    const int MaxChain = 31;

    readonly List<int> _building = new();
    readonly Dictionary<int, int> _buildPointers = new(); // pointer -> last pattern cell
    readonly List<(Rect Rect, IReadOnlyList<int> Chain)> _savedHits = new();

    public ProjectScreen(AppModel app) : base(app.Session) { }

    public override string Title => $"PROJECT {Session.Chain.Project + 1}";

    static Rect PatternRect(int p) => new(ColLabel + p % 8 * ColW, Row0 + p / 8 * (Row1 - Row0), ColW, Row1 - Row0);

    static int PatternAt(Point p)
    {
        for (int i = 0; i < PatternBank.PatternCount; i++)
            if (PatternRect(i).Contains(p)) return i;
        return -1;
    }

    // ------------------------------------------------------------------ render

    protected override void Render(DrawingContext dc)
    {
        _savedHits.Clear();
        Draw.Label(dc, Title, DesignWidth / 2, TitleY, 56, Theme.Ink, Align.Center, Theme.Regular);

        Draw.HLine(dc, LeftEdge, RightEdge, Row0);
        Draw.HLine(dc, ColLabel, GridEnd, Row1);
        Draw.HLine(dc, LeftEdge, RightEdge, Row2);
        Draw.HLine(dc, LeftEdge, RightEdge, Row3);
        Draw.VLine(dc, ColLabel, Row0, BottomEdge);
        Draw.VLine(dc, GridEnd, Row0, BottomEdge);
        for (int i = 1; i < 8; i++) Draw.VLine(dc, ColLabel + i * ColW, Row0, Row2);

        DrawPatterns(dc);
        DrawChain(dc);
        DrawSavedChains(dc);
        DrawStatus(dc);
    }

    void DrawPatterns(DrawingContext dc)
    {
        Draw.Label(dc, "PATTERNS", 20, Row0 + 42, 30, Theme.Mid);
        var bank = Session.Bank;
        var inChain = Session.Chain.Chain;
        for (int p = 0; p < PatternBank.PatternCount; p++)
        {
            var r = PatternRect(p);
            Draw.Label(dc, (p + 1).ToString(), r.X + 10, r.Y + 26, 40, Theme.Mid);
            bool filled = bank is not null && !bank.IsEmpty(p);
            dc.DrawEllipse(filled ? Theme.Ink : Theme.Rule, null, new Point(r.X + ColW / 2, r.Y + 84), 11, 11);
            if (inChain.Count > 1 && inChain.Contains(p))
                dc.DrawEllipse(Theme.Green, null, new Point(r.Right - 18, r.Y + 22), 5, 5);
            var order = _building.Select((x, i) => (x, i)).Where(e => e.x == p).Select(e => (e.i + 1).ToString()).ToList();
            if (order.Count > 0)
                Draw.Label(dc, string.Join(" ", order), r.X + ColW / 2, r.Bottom - 16, 22, Theme.Red, Align.Center, Theme.Regular);
        }
        Draw.Frame(dc, PatternRect(Session.ActivePattern), Theme.Yellow);
        foreach (var p in _building.Distinct()) Draw.Frame(dc, PatternRect(p), Theme.Red);
    }

    void DrawChain(DrawingContext dc)
    {
        Draw.Label(dc, "CHAIN", 20, Row2 + 42, 30, Theme.Mid);
        IReadOnlyList<int> chain = _building.Count > 0 ? _building : Session.Chain.Chain;
        if (chain.Count == 0)
        {
            Draw.Label(dc, "-", ColLabel + 20, Row2 + 65, 32, Theme.Mid);
            return;
        }
        double w = Math.Min(ColW / 2, (GridEnd - ColLabel) / chain.Count);
        double top = Row2 + 18, h = 64;
        for (int i = 0; i < chain.Count; i++)
        {
            double x = ColLabel + i * w;
            if (i > 0) Draw.VLine(dc, x, top + 8, top + h - 8, i % 4 == 0 ? new Pen(Theme.Ink, 2) : Theme.RulePen);
            Draw.Label(dc, (chain[i] + 1).ToString(), x + w / 2, top + h / 2, w < 40 ? 24 : 32, Theme.Ink, Align.Center, Theme.Regular);
        }
        if (_building.Count == 0)
        {
            int pos = Session.Chain.Position - 1;
            if (pos >= 0 && pos < chain.Count) Draw.Frame(dc, new Rect(ColLabel + pos * w, top, w, h), Theme.Yellow);
        }
        string caption = _building.Count > 0 ? "RELEASE TO RECALL" : $"{Math.Max(1, Session.Chain.Position)} / {chain.Count}";
        Draw.Label(dc, caption, ColLabel + 14, Row3 - 26, 24, Theme.Mid);
    }

    void DrawSavedChains(DrawingContext dc)
    {
        Draw.Label(dc, "SAVED", 20, Row3 + 42, 30, Theme.Mid);
        Draw.Label(dc, "CHAINS", 20, Row3 + 78, 30, Theme.Mid);
        var global = Session.Global;
        double w = (GridEnd - ColLabel) / 5, h = (DesignHeight - Row3) / 3;
        for (int i = 1; i < 5; i++) Draw.VLine(dc, ColLabel + i * w, Row3, BottomEdge);
        for (int i = 1; i < 3; i++) Draw.HLine(dc, ColLabel, GridEnd, Row3 + i * h);
        for (int slot = 0; slot < 15; slot++)
        {
            var r = new Rect(ColLabel + slot % 5 * w, Row3 + slot / 5 * h, w, h);
            var chain = global?.Chain(slot) ?? [];
            Draw.Label(dc, (slot + 1).ToString(), r.X + 12, r.Y + 26, 30, Theme.Mid);
            if (chain.Count == 0)
                Draw.Label(dc, "EMPTY", r.X + 12, r.Y + 72, 26, Theme.Rule);
            else
            {
                Draw.Label(dc, chain.Count.ToString(), r.Right - 12, r.Y + 26, 24, Theme.Mid, Align.Right);
                Draw.Label(dc, Fit(string.Join(" ", chain.Select(x => x + 1)), w - 24, 26), r.X + 12, r.Y + 72, 26, Theme.Ink, Align.Left, Theme.Regular);
                _savedHits.Add((r, chain));
            }
            if (global?.SelectedChain == slot) Draw.Frame(dc, r, Theme.Yellow);
        }
    }

    void DrawStatus(DrawingContext dc)
    {
        var ps = Session.PatternState;
        string clipboard = ps.ProjectCopied ? "PROJECT" : ps.PatternCopied ? "PATTERN" : "-";
        (string Label, string Value, Brush Brush)[] items =
        [
            ("PATTERN", (Session.ActivePattern + 1).ToString(), Theme.Ink),
            ("SAVE", ps.Autosave ? "AUTO" : "MANUAL", ps.Autosave ? Theme.Green : Theme.Ink),
            ("CHAIN MODE", ps.ChainMode ? "ON" : "OFF", ps.ChainMode ? Theme.Green : Theme.Ink),
            ("COPIED", clipboard, Theme.Ink),
        ];
        double[] rows = [Row0, Row1, Row2, Row3];
        double cx = (GridEnd + DesignWidth) / 2;
        Draw.HLine(dc, GridEnd, RightEdge, Row1);
        for (int i = 0; i < items.Length; i++)
        {
            Draw.Label(dc, items[i].Label, GridEnd + 18, rows[i] + 42, 30, Theme.Mid);
            Draw.Label(dc, items[i].Value, cx, rows[i] + 90, 42, items[i].Brush, Align.Center, Theme.Regular);
        }
    }

    static string Fit(string text, double width, double size)
    {
        if (Draw.Text(text, size, Theme.Ink, Theme.Regular).Width <= width) return text;
        while (text.Length > 1 && Draw.Text(text + "…", size, Theme.Ink, Theme.Regular).Width > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerDown(int id, Point p)
    {
        int pattern = PatternAt(p);
        if (pattern >= 0)
        {
            _buildPointers[id] = pattern;
            if (_building.Count < MaxChain) _building.Add(pattern);
            return;
        }
        if (_buildPointers.Count > 0) return;
        foreach (var (rect, chain) in _savedHits)
            if (rect.Contains(p)) { Session.SelectPatterns(chain); return; }
    }

    protected override void OnPointerMove(int id, Point p, Vector delta)
    {
        if (!_buildPointers.TryGetValue(id, out var last)) return;
        int pattern = PatternAt(p);
        if (pattern < 0 || pattern == last) return;
        _buildPointers[id] = pattern;
        if (_building.Count < MaxChain) _building.Add(pattern);
        InvalidateVisual();
    }

    protected override void OnPointerUp(int id, Point p)
    {
        if (!_buildPointers.Remove(id) || _buildPointers.Count > 0) return;
        if (_building.Count > 0) Session.SelectPatterns(_building.ToList());
        _building.Clear();
    }
}
