using OPB.Core;
using OPB.Core.Midi;
using OPB.Core.Model;

namespace OPB.App;

public enum AppPage { Screen, Configurator, Photomatic, Motion, MidiSetup, Devices }

/// <summary>App-level UI state shared by the screens (not stored on the OP-Z).</summary>
public sealed class AppModel
{
    public AppModel(OpzSession session, OpzBluetooth bluetooth)
    {
        Session = session;
        Bluetooth = bluetooth;
        if (BluetoothSettings.Load() is { } saved)
        {
            BluetoothName = saved.Name;
            bluetooth.Target = saved.Address;
        }
    }

    public OpzSession Session { get; }

    /// <summary>The Bluetooth link, used when no OP-Z is plugged in over USB.</summary>
    public OpzBluetooth Bluetooth { get; }

    /// <summary>Name of the saved Bluetooth OP-Z (<see cref="OpzBluetooth.Target"/>).</summary>
    public string? BluetoothName { get; private set; }

    /// <summary>Connects to this OP-Z over Bluetooth from now on (when USB isn't plugged in) and remembers it.</summary>
    public void UseBluetooth(BleDevice device)
    {
        BluetoothName = string.IsNullOrWhiteSpace(device.Name) ? "OP-Z" : device.Name;
        Bluetooth.Target = device.Address;
        BluetoothSettings.Save(device.Address, BluetoothName);
        if (Session.Connection is BleMidiConnection link && link.Address != device.Address) Session.Reconnect();
    }

    /// <summary>Stops using Bluetooth and drops a Bluetooth link in use.</summary>
    public void ForgetBluetooth()
    {
        BluetoothName = null;
        Bluetooth.Target = null;
        BluetoothSettings.Save(null, null);
        if (Session.Connection is BleMidiConnection) Session.Reconnect();
    }

    public AppPage Page { get; private set; } = AppPage.Screen;
    public event Action? PageChanged;

    public void Navigate(AppPage page)
    {
        if (Page == page) return;
        Page = page;
        PageChanged?.Invoke();
    }

    /// <summary>The parameter last tapped on each track; the touch pad edits it.</summary>
    readonly Dictionary<int, ParamRef> _selected = new();
    public event Action? SelectionChanged;

    public ParamRef Selected(int track) =>
        _selected.TryGetValue(track, out var p) ? p : ParamRef.Sound(Session, track, SoundParam.P1);

    public void Select(int track, ParamRef param)
    {
        _selected[track] = param;
        SelectionChanged?.Invoke();
    }

    /// <summary>Plays (velocity &gt; 0) or releases a note on the track's configured MIDI channel.</summary>
    public void SendTrackNote(int track, int note, int velocity)
    {
        int channel = Session.MidiConfig?.Channel(track) ?? track;
        if (velocity > 0) Session.SendShort((byte)(0x90 | channel), (byte)Math.Clamp(note, 0, 127), (byte)Math.Clamp(velocity, 1, 127));
        else Session.SendShort((byte)(0x80 | channel), (byte)Math.Clamp(note, 0, 127), 0);
    }

    /// <summary>Sends a CC on the track's configured MIDI channel (used for track settings).</summary>
    public void SendTrackCc(int track, int cc, int value)
    {
        int channel = Session.MidiConfig?.Channel(track) ?? track;
        Session.SendShort((byte)(0xB0 | channel), (byte)cc, (byte)Math.Clamp(value, 0, 127));
    }
}

/// <summary>Something the touch pad can edit: a 0..255 value with a label and formatting.</summary>
public sealed class ParamRef : IEquatable<ParamRef>
{
    readonly Func<int> _get;
    readonly Action<int> _set;
    readonly Func<int, string> _format;

    public ParamRef(string key, string label, Func<int> get, Action<int> set, Func<int, string> format, int min = 0, int max = 255, double sensitivity = 1)
    {
        Key = key;
        Label = label;
        _get = get;
        _set = set;
        _format = format;
        Min = min;
        Max = max;
        Sensitivity = sensitivity;
    }

    public string Key { get; }
    public string Label { get; }
    public int Min { get; }
    public int Max { get; }
    /// <summary>Value units per design pixel of touch pad travel.</summary>
    public double Sensitivity { get; }
    public int Value => _get();
    public string Text => _format(Value);
    public void Set(int value) => _set(Math.Clamp(value, Min, Max));

    public static ParamRef Sound(OpzSession s, int track, SoundParam p, string? label = null, Func<int, string>? format = null) =>
        new($"sound:{track}:{p}", label ?? p.ToString().ToUpperInvariant(),
            () => s.CurrentSound[track][(int)p],
            v => s.SetSound(track, p, (byte)v),
            format ?? (v => Display.Percent((byte)v).ToString()));

    public bool Equals(ParamRef? other) => other is not null && other.Key == Key;
    public override bool Equals(object? obj) => Equals(obj as ParamRef);
    public override int GetHashCode() => Key.GetHashCode();
}
