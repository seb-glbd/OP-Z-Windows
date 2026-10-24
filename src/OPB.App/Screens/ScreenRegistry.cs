using System.Reflection;
using System.Windows.Media;
using OPB.App.Rendering;
using OPB.Core.Model;

namespace OPB.App.Screens;

/// <summary>Marks a ScreenView subclass as the screen for a route key. It needs a ctor taking AppModel.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ScreenAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

public static class ScreenKeys
{
    public const string Track = "track";
    public const string Mixer = "mixer";
    public const string Tempo = "tempo";
    public const string Project = "project";
    public const string Tape = "tape";
    public const string Perform = "perform";
    public const string MasterTrack = "master";
    public const string ModuleTrack = "module";
    public const string LightsTrack = "lights";
    public const string MotionTrack = "motiontrack";
    public const string StepComponents = "steps";
    public const string Configurator = "configurator";
    public const string Photomatic = "photomatic";
    public const string Motion = "motion";
    public const string MidiSetup = "midisetup";
    public const string Devices = "devices";
}

/// <summary>Finds screens by route key and decides which one the "screen" page should show.</summary>
public static class ScreenRegistry
{
    static readonly Dictionary<string, Type> Types = Assembly.GetExecutingAssembly().GetTypes()
        .Where(t => typeof(ScreenView).IsAssignableFrom(t) && t.GetCustomAttribute<ScreenAttribute>() is not null)
        .ToDictionary(t => t.GetCustomAttribute<ScreenAttribute>()!.Key);

    public static bool Has(string key) => key == ScreenKeys.Track || Types.ContainsKey(key);

    public static ScreenView Create(string key, AppModel app)
    {
        if (key == ScreenKeys.Track) return new TrackScreen(app);
        if (Types.TryGetValue(key, out var type)) return (ScreenView)Activator.CreateInstance(type, app)!;
        return new PlaceholderScreen(app, key);
    }

    /// <summary>The route for the main screen, following held buttons and the active track like the device.</summary>
    public static string RouteFor(AppModel app)
    {
        var s = app.Session;
        return app.Page switch
        {
            AppPage.Configurator => ScreenKeys.Configurator,
            AppPage.Photomatic => ScreenKeys.Photomatic,
            AppPage.Motion => ScreenKeys.Motion,
            AppPage.MidiSetup => ScreenKeys.MidiSetup,
            AppPage.Devices => ScreenKeys.Devices,
            _ when s.Ui.Mixer && Has(ScreenKeys.Mixer) => ScreenKeys.Mixer,
            _ when s.Ui.Tempo && Has(ScreenKeys.Tempo) => ScreenKeys.Tempo,
            _ when s.Ui.Project && Has(ScreenKeys.Project) => ScreenKeys.Project,
            _ when s.Ui.HeldKey >= 0 && s.Ui.Shift && Tracks.HasSound(s.ActiveTrack) && Has(ScreenKeys.StepComponents) => ScreenKeys.StepComponents,
            _ when (TrackId)s.ActiveTrack == TrackId.Tape && Has(ScreenKeys.Tape) => ScreenKeys.Tape,
            _ when (TrackId)s.ActiveTrack == TrackId.Perform && Has(ScreenKeys.Perform) => ScreenKeys.Perform,
            _ when (TrackId)s.ActiveTrack == TrackId.Master => ScreenKeys.MasterTrack,
            _ when (TrackId)s.ActiveTrack == TrackId.Module => ScreenKeys.ModuleTrack,
            _ when (TrackId)s.ActiveTrack == TrackId.Lights => ScreenKeys.LightsTrack,
            _ when (TrackId)s.ActiveTrack == TrackId.Motion => ScreenKeys.MotionTrack,
            _ => ScreenKeys.Track,
        };
    }
}

/// <summary>Shown for a route whose screen isn't available.</summary>
public sealed class PlaceholderScreen(AppModel app, string key) : ScreenView(app.Session)
{
    public override string Title => key.ToUpperInvariant();

    protected override void Render(DrawingContext dc)
    {
        Draw.Label(dc, Title, DesignWidth / 2, 40, 56, Theme.Ink, Align.Center, Theme.Regular);
        Draw.HLine(dc, 0, DesignWidth, 82);
    }
}
