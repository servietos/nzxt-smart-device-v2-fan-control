using System.Diagnostics;
using System.IO;

namespace NzxtFan;

/// Steuert die Beleuchtung über OpenRGB in einem eigenen Thread (blockiert nie die Lüfterregelung).
/// Startet OpenRGB bei Bedarf unsichtbar als Server und beendet es wieder, wenn es selbst gestartet wurde.
public sealed class LightingEngine : IDisposable
{
    private readonly Sensors _sensors;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private readonly object _lock = new();

    private OpenRgbClient? _client;
    private Process? _ownServer;
    private List<RgbDevice> _devices = new();
    private DateTime _lastDeviceScan = DateTime.MinValue, _lastStart = DateTime.MinValue, _lastFullRefresh = DateTime.MinValue;
    private int _stableScans;

    // Vorgaben von der Oberfläche (immer als Kopie übergeben)
    private List<LightSetting> _lights = new();
    private HashSet<string> _enabled = new();
    private bool _active;
    private string _serverPath = "";
    private volatile bool _force = true;

    // Zustand je Gerät/Zone, damit nur bei Änderungen gesendet wird
    private readonly Dictionary<string, string> _deviceSig = new();
    private readonly Dictionary<(string, int), uint> _zoneColor = new();
    private readonly Dictionary<(string, int), (byte r, byte g, byte b)> _shown = new();

    public string Status { get; private set; } = "";
    public bool Connected => _client?.Connected == true;

    private readonly FanController _fans;
    private bool _quietHeld;                      // wir haben beim Lüfter-Controller ein Ruhefenster angefordert
    private DateTime _quietHeldSince;

    /// fans: der Lüfter-Controller muss schweigen, während OpenRGB startet und das LED-Zubehör erkennt (siehe FanController, Ruhefenster).
    public LightingEngine(Sensors sensors, FanController fans)
    {
        _sensors = sensors; _fans = fans;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Lighting" };
        _thread.Start();
    }

    /// Neue Vorgaben übernehmen (Profilwechsel, Bearbeitung im Fenster, Geräte an/aus).
    public void Configure(bool active, string serverPath, IEnumerable<string> enabledDevices, IEnumerable<LightSetting> lights)
    {
        lock (_lock)
        {
            _active = active; _serverPath = serverPath;
            _enabled = new HashSet<string>(enabledDevices);
            _lights = lights.Select(l => l.Clone()).ToList();
        }
        _force = true;
    }

    public List<RgbDevice> Devices { get { lock (_lock) return _devices.ToList(); } }

    /// Alles beim nächsten Durchlauf neu senden (z. B. nach Standby).
    public void Refresh() => _force = true;

    /// Zuletzt gesetzte Farbe einer Zone (für Vorschau und Akzentfarbe). Zone -1 = erste Zone mit Farbe.
    public (byte r, byte g, byte b)? ShownColor(string device, int zone)
    {
        lock (_lock)
        {
            if (_shown.TryGetValue((device, zone), out var c)) return c;
            var any = _shown.FirstOrDefault(kv => kv.Key.Item1 == device);
            return any.Key.Item1 == null ? null : any.Value;
        }
    }

    private void Loop()
    {
        while (_running)
        {
            try { Step(); }
            catch (Exception ex)
            {
                FanController.Log("Beleuchtung: " + ex.Message);
                Status = Loc.T("LightLost");
                EndQuiet();
                _client?.Dispose(); _client = null;
                lock (_lock) { _deviceSig.Clear(); _zoneColor.Clear(); }
            }
            for (int i = 0; i < 10 && _running && !_force; i++) Thread.Sleep(100);
        }
    }

    private void Step()
    {
        // Erst den ersten Verbindungsversuch der Lüftersteuerung abwarten, sonst käme deren Einrichtung OpenRGB in die Quere
        if (!_fans.FirstTickDone) { Thread.Sleep(200); return; }
        // Ruhefenster nie hängen lassen (z. B. wenn OpenRGB nicht startet): nach einer Minute freigeben und neu anfangen
        if (_quietHeld && DateTime.UtcNow - _quietHeldSince > TimeSpan.FromSeconds(60)) EndQuiet();
        bool active; string path; HashSet<string> enabled; List<LightSetting> lights;
        lock (_lock) { active = _active; path = _serverPath; enabled = _enabled; lights = _lights; }
        bool force = _force; _force = false;

        if (!active || enabled.Count == 0)
        {
            EndQuiet();
            if (_client != null || _ownServer != null) { Disconnect(); Status = Loc.T("LightOff"); }
            Status = Loc.T("LightOff");
            return;
        }

        if (_client == null || !_client.Connected) { Connect(path); if (_client == null) return; force = true; }

        // Geräteliste: am Anfang wiederholt, bis sie stabil ist (Erkennung dauert einige Sekunden), danach bei Änderungsmeldung
        bool scanDue = _stableScans < 3 ? DateTime.UtcNow - _lastDeviceScan > TimeSpan.FromSeconds(2) : _client.DeviceListChanged;
        if (scanDue)
        {
            _client.DeviceListChanged = false;
            var devs = _client.GetDevices();
            _lastDeviceScan = DateTime.UtcNow;
            List<RgbDevice> old; lock (_lock) { old = _devices; _devices = devs; }
            _stableScans = devs.Count > 0 && devs.Count == old.Count ? _stableScans + 1 : 0;
            if (devs.Count != old.Count) { force = true; lock (_lock) { _deviceSig.Clear(); _zoneColor.Clear(); } }
        }
        // Erkennung abgeschlossen (Geräteliste stabil) oder zu lange gewartet: Lüfter-Controller wieder freigeben
        if (_quietHeld && (_stableScans >= 3 || DateTime.UtcNow - _quietHeldSince > TimeSpan.FromSeconds(35))) EndQuiet();
        if (_quietHeld) { Status = Loc.T("LightStarting"); return; }   // noch keine Farben senden: jeder Befehl löst eine Meldung des Controllers aus
        // alle 30 s alles erneut senden (falls ein anderes Programm dazwischengefunkt hat oder nach Standby)
        if (DateTime.UtcNow - _lastFullRefresh > TimeSpan.FromSeconds(30)) { force = true; _lastFullRefresh = DateTime.UtcNow; }

        List<RgbDevice> devices; lock (_lock) devices = _devices;
        foreach (var d in devices.Where(d => enabled.Contains(d.Name)))
            ApplyDevice(d, lights.Where(l => l.Device == d.Name).ToList(), force);
        Status = Loc.T("LightOk", devices.Count(d => enabled.Contains(d.Name)));
    }

    private void ApplyDevice(RgbDevice d, List<LightSetting> settings, bool force)
    {
        var c = _client!;
        var effect = settings.FirstOrDefault(s => s.Zone < 0 && s.Mode == LightMode.Effect);
        if (effect != null)
        {
            var sig = "effect:" + effect.Signature;
            if (!force && _deviceSig.GetValueOrDefault(d.Name) == sig) return;
            var mode = d.Modes.FirstOrDefault(m => m.Name == effect.Effect) ?? d.Modes.FirstOrDefault(m => !m.IsDirect);
            if (mode == null) return;
            var m = Copy(mode);
            if (m.HasSpeed) m.Speed = (uint)Math.Round(m.SpeedMin + ((double)m.SpeedMax - m.SpeedMin) * Math.Clamp(effect.Speed, 0, 100) / 100.0);
            if (m.HasBrightness) m.Brightness = (uint)Math.Round(m.BrightnessMin + ((double)m.BrightnessMax - m.BrightnessMin) * Math.Clamp(effect.Brightness, 0, 100) / 100.0);
            var dimTo = m.HasBrightness ? 100 : effect.Brightness;
            if (m.UsesModeColors)
            {
                var cols = effect.EffectColors.Select(h => Rgb.Dim(Rgb.Parse(h), dimTo)).Select(x => OpenRgbClient.Rgb(x.r, x.g, x.b)).ToList();
                while (cols.Count < m.ColorsMin) cols.Add(cols.Count > 0 ? cols[^1] : OpenRgbClient.Rgb(255, 255, 255));
                m.Colors = cols.Take((int)m.ColorsMax).ToArray();
            }
            c.SetMode(d.Index, m);
            lock (_lock)
            {
                _deviceSig[d.Name] = sig;
                foreach (var z in d.Zones) _shown[(d.Name, z.Index)] = effect.EffectColors.Count > 0 ? Rgb.Dim(Rgb.Parse(effect.EffectColors[0]), effect.Brightness) : ((byte)0, (byte)0, (byte)0);
                _zoneColor.Clear();
            }
            return;
        }

        // Zonen einzeln: Gerät in den Direktmodus schalten, dann Farben je Zone setzen
        if (force || _deviceSig.GetValueOrDefault(d.Name) != "direct")
        {
            var direct = d.Modes.FirstOrDefault(m => m.IsDirect);
            if (direct != null) c.SetMode(d.Index, Copy(direct)); else c.SetDirectMode(d.Index);
            lock (_lock) { _deviceSig[d.Name] = "direct"; foreach (var z in d.Zones) _zoneColor.Remove((d.Name, z.Index)); }
        }
        foreach (var z in d.Zones)
        {
            if (z.LedsCount == 0) continue;
            var s = settings.FirstOrDefault(x => x.Zone == z.Index);
            (byte r, byte g, byte b) col = (0, 0, 0);
            if (s != null)
            {
                col = s.Mode switch
                {
                    LightMode.Static => Rgb.Parse(s.Color),
                    LightMode.Temperature => _sensors.Get(s.Source) is double t ? Rgb.Gradient(s.Stops, t) : Rgb.Parse(s.Stops.OrderBy(x => x.Temp).LastOrDefault()?.Color ?? "#FF0000"),
                    _ => (0, 0, 0)
                };
                col = Rgb.Dim(col, s.Brightness);
            }
            uint packed = OpenRgbClient.Rgb(col.r, col.g, col.b);
            if (!force && _zoneColor.TryGetValue((d.Name, z.Index), out var last) && last == packed) continue;
            c.SetZoneColors(d.Index, z.Index, Enumerable.Repeat(packed, (int)z.LedsCount).ToArray());
            lock (_lock) { _zoneColor[(d.Name, z.Index)] = packed; _shown[(d.Name, z.Index)] = col; }
        }
    }

    private static RgbMode Copy(RgbMode m) => new()
    {
        Index = m.Index, Name = m.Name, Value = m.Value, Flags = m.Flags, SpeedMin = m.SpeedMin, SpeedMax = m.SpeedMax,
        BrightnessMin = m.BrightnessMin, BrightnessMax = m.BrightnessMax, ColorsMin = m.ColorsMin, ColorsMax = m.ColorsMax,
        Speed = m.Speed, Brightness = m.Brightness, Direction = m.Direction, ColorMode = m.ColorMode, Colors = m.Colors.ToArray()
    };

    private void Connect(string path)
    {
        var c = new OpenRgbClient();
        try { c.Connect(); _client = c; _stableScans = 0; FanController.Log($"OpenRGB verbunden (Protokoll {c.ServerProtocol})."); return; }
        catch { c.Dispose(); }

        // Kein Server erreichbar: OpenRGB selbst starten (höchstens alle 20 s versuchen)
        if (DateTime.UtcNow - _lastStart < TimeSpan.FromSeconds(20)) { Status = Loc.T("LightStarting"); return; }
        _lastStart = DateTime.UtcNow;
        var exe = FindServer(path);
        if (exe == null) { Status = Loc.T("LightNoServer"); return; }
        if (Process.GetProcessesByName("OpenRGB").Length > 0) { Status = Loc.T("LightForeign"); return; }   // läuft schon, aber ohne Server
        // Ruhefenster: erst wenn der Controller keine Statusmeldungen mehr sendet, darf OpenRGB das LED-Zubehör abfragen
        if (!_quietHeld) { _fans.RequestQuiet(); _quietHeld = true; _quietHeldSince = DateTime.UtcNow; }
        if (!_fans.QuietActive) { _lastStart = DateTime.MinValue; Status = Loc.T("LightStarting"); return; }   // gleich im nächsten Durchlauf erneut prüfen
        Thread.Sleep(1500);   // bereits gesendete Meldungen abklingen lassen
        _quietHeldSince = DateTime.UtcNow;
        _ownServer = Process.Start(new ProcessStartInfo(exe, "--server --noautoconnect")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        FanController.Log("OpenRGB gestartet: " + exe);
        Status = Loc.T("LightStarting");
    }

    /// Sucht OpenRGB.exe: eingestellter Pfad, Unterordner „OpenRGB“ neben dem Programm, übliche Installationsorte.
    public static string? FindServer(string configured)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(baseDir, "OpenRGB", "OpenRGB.exe"));
        candidates.Add(Path.Combine(baseDir, "..", "OpenRGB", "OpenRGB Windows 64-bit", "OpenRGB.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"));
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private void EndQuiet() { if (_quietHeld) { _fans.ReleaseQuiet(); _quietHeld = false; } }

    private void Disconnect()
    {
        EndQuiet();
        _client?.Dispose(); _client = null;
        if (_ownServer is { HasExited: false }) { try { _ownServer.Kill(); } catch { } }
        _ownServer = null;
        lock (_lock) { _devices = new(); _deviceSig.Clear(); _zoneColor.Clear(); _shown.Clear(); }
        _stableScans = 0;
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(3000);
        Disconnect();
    }
}
