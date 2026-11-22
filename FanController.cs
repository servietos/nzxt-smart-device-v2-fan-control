using System.IO;

namespace NzxtFan;

/// Regelt die Lüfter einmal pro Sekunde nach der aktiven Kurve.
public sealed class FanController : IDisposable
{
    public const int FailsafePercent = 60;   // wenn ein Temperaturfühler ausfällt
    public const int ExitPercent = 50;       // beim Beenden, damit die Lüfter nie stehen bleiben

    private readonly AppConfig _cfg;
    private readonly Sensors _sensors;
    private SmartDevice2? _dev;
    private readonly double?[] _smoothTemp = new double?[3];
    private readonly double[] _target = { -1, -1, -1 };
    private readonly int[] _sent = { -1, -1, -1 };
    private DateTime _lastReconnect = DateTime.MinValue;
    private DateTime _lastResend = DateTime.MinValue;
    private bool _releasedFans;

    public string Status { get; private set; } = Loc.T("Starting");
    public bool Connected => _dev != null && (_quietActive || DateTime.UtcNow - _dev.LastStatus < TimeSpan.FromSeconds(5));
    private static readonly FanState[] NoFans = { new(), new(), new() };
    public FanState[] Fans => _dev?.Fans ?? NoFans;
    public double?[] ChannelTemp => _smoothTemp;
    public int[] TargetPercent => _target.Select(t => (int)Math.Round(Math.Max(0, t))).ToArray();
    public Sensors Sensors => _sensors;

    public FanController(AppConfig cfg, Sensors sensors) { _cfg = cfg; _sensors = sensors; }

    public static void Log(string msg)
    {
        // Wird aus mehreren Threads aufgerufen (Oberfläche, HID-Leser, Beleuchtung): nacheinander schreiben, sonst gehen Zeilen verloren
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(AppConfig.Dir);
                var file = Path.Combine(AppConfig.Dir, "log.txt");
                // Protokoll begrenzen: ab 1 MB die alte Datei als log.alt.txt ablegen und neu beginnen
                if (new FileInfo(file) is { Exists: true, Length: > 1_000_000 }) File.Move(file, Path.Combine(AppConfig.Dir, "log.alt.txt"), true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
    private static readonly object LogLock = new();

    private void Connect()
    {
        _lastReconnect = DateTime.UtcNow;
        try
        {
            _dev?.Dispose();
            _dev = SmartDevice2.Find(Log);
            if (_dev == null) { Status = Loc.T("NotFound"); return; }
            // Im Ruhefenster gleich ohne Statusmeldungen öffnen
            _quietActive = _quietWanted; _quietSince = DateTime.UtcNow;
            _dev.Open(_quietActive ? SmartDevice2.StatusQuiet : SmartDevice2.StatusNormal);
            Array.Fill(_sent, -1);
            _connectedAt = DateTime.UtcNow;
            _releasedFans = false;   // neu verbundenes Gerät (z. B. nach Standby) bekommt die 50 % erneut, falls die Steuerung abgeschaltet ist
            Status = Loc.T("Connected", _dev.Description);
            Log(Status);
        }
        catch (Exception ex)
        {
            _dev?.Dispose(); _dev = null;
            Status = Loc.T("Busy");
            Log("Verbinden fehlgeschlagen: " + ex.Message);
        }
    }

    /// Nach Standby: Gerät neu öffnen und Werte erneut senden.
    public void Resume() { Log("Aufgewacht, verbinde neu."); _lastReconnect = DateTime.MinValue; _dev?.Dispose(); _dev = null; }

    // ---------- Ruhefenster für OpenRGB ----------
    // OpenRGB fragt beim Start das LED-Zubehör des Controllers ab, wertet aber die erste Meldung aus, die es liest.
    // Sendet der Controller gerade Statusmeldungen (2× pro Sekunde, seit wir ihn eingerichtet haben), hält OpenRGB eine
    // davon für die Antwort und errechnet aus den Lüfterdaten falsche LED-Zahlen (z. B. 26 an Kanal 1, 0 an Kanal 2 →
    // ein Teil der Beleuchtung bleibt dunkel). Deshalb schaltet die Beleuchtung die Statusmeldungen für die Dauer der
    // Erkennung ab. Auch Stellbefehle ruhen so lange, weil der Controller jeden Befehl mit einer Meldung quittiert.
    private volatile bool _quietWanted, _quietActive, _firstTickDone;
    private DateTime _quietSince = DateTime.MinValue;
    private static readonly TimeSpan QuietMax = TimeSpan.FromSeconds(45);   // Obergrenze: die Lüfterregelung darf nie lange ruhen

    /// Von der Beleuchtung (anderer Thread): Ruhe anfordern bzw. wieder freigeben. Umgesetzt wird im nächsten Tick.
    public void RequestQuiet() => _quietWanted = true;
    public void ReleaseQuiet() => _quietWanted = false;
    /// true, sobald keine Statusmeldungen mehr kommen (oder gar kein Controller verbunden ist).
    public bool QuietActive => _quietActive || (_firstTickDone && _dev == null);
    /// Der erste Verbindungsversuch ist gelaufen; vorher darf die Beleuchtung OpenRGB nicht starten.
    public bool FirstTickDone => _firstTickDone;

    private void UpdateQuiet()
    {
        if (_quietActive && (!_quietWanted || DateTime.UtcNow - _quietSince > QuietMax))
        {
            bool timedOut = _quietWanted;
            _quietWanted = false; _quietActive = false;
            try { _dev?.SetStatusInterval(SmartDevice2.StatusNormal); } catch (Exception ex) { Log("Statusmeldungen einschalten: " + ex.Message); }
            _lastReconnect = DateTime.UtcNow;    // dem Controller Zeit für die erste Statusmeldung geben, statt gleich neu zu verbinden
            _connectedAt = DateTime.UtcNow;      // Drehzahlen sind noch alt: Stillstandsschutz kurz aussetzen
            Log(timedOut ? "Ruhefenster für OpenRGB nach Zeitablauf beendet." : "Ruhefenster für OpenRGB beendet.");
        }
        else if (_quietWanted && !_quietActive && _dev != null)
        {
            try { _dev.SetStatusInterval(SmartDevice2.StatusQuiet); _quietActive = true; _quietSince = DateTime.UtcNow; Log("Ruhefenster für OpenRGB: Statusmeldungen aus."); }
            catch (Exception ex) { Log("Statusmeldungen ausschalten: " + ex.Message); _dev.Dispose(); _dev = null; }
        }
    }

    public int Ticks { get; private set; }

    public void Tick()
    {
        try { TickCore(); } finally { _firstTickDone = true; }
    }

    private void TickCore()
    {
        Ticks++;
        // Sensorfehler (z. B. nach Standby) dürfen die Regelung nie anhalten: Temperaturen gelten dann als unbekannt → 60 %
        try { _sensors.Update(); } catch (Exception ex) { _sensors.Invalidate(); LogOnce("Sensoren: " + ex.Message); }
        UpdateQuiet();
        if ((_dev == null || !Connected) && DateTime.UtcNow - _lastReconnect > TimeSpan.FromSeconds(8)) Connect();

        var prof = _cfg.Active;
        for (int i = 0; i < 3; i++)
        {
            double want;
            try
            {
                var fa = prof.Fans[i];
                var curve = _cfg.CurveById(fa.CurveId);
                var t = _sensors.Get(fa.Source);
                // Temperatur glätten, damit die Lüfter nicht bei jeder kurzen Spitze hochdrehen
                _smoothTemp[i] = t == null ? null : _smoothTemp[i] is double old ? old + (t.Value - old) * 0.25 : t;
                // Ohne Temperatur oder ohne Kurve: Sicherheitsdrehzahl
                want = _smoothTemp[i] is double st && curve != null ? Math.Max(Curve.Evaluate(curve.Percent, st), SafetyFloor(t ?? st)) : FailsafePercent;
            }
            catch (Exception ex) { want = FailsafePercent; LogOnce($"Regelung Lüfter {i + 1}: {ex.Message}"); }
            // Rampe: schnell hoch (max. 10 %/s), langsam runter (max. 3 %/s)
            if (_target[i] < 0) _target[i] = want;
            else _target[i] += Math.Clamp(want - _target[i], -3, 10);
        }

        if (_dev == null || _quietActive || _dev.LastStatus == DateTime.MinValue) return;
        // Lüftersteuerung in den Einstellungen abgeschaltet: einmal sicher auf 50 % stellen, danach nichts mehr senden
        if (!_cfg.FansEnabled)
        {
            if (!_releasedFans) { for (int i = 0; i < 3; i++) { try { _dev.SetDuty(i, ExitPercent); } catch { } } _releasedFans = true; Array.Fill(_sent, -1); Log("Lüftersteuerung abgeschaltet, Lüfter auf 50 %."); }
            return;
        }
        _releasedFans = false;
        bool resend = DateTime.UtcNow - _lastResend > TimeSpan.FromSeconds(3);
        for (int i = 0; i < 3; i++)
        {
            int d = StallGuard(i, (int)Math.Round(_target[i]));
            // Senden bei Änderung ≥ 1 %, oder erneut wenn der Controller den Wert nicht übernommen hat
            if (d != _sent[i] || (resend && _dev.Fans[i].Duty != d))
            {
                try { _dev.SetDuty(i, d); _sent[i] = d; } catch (Exception ex) { Log($"Setzen Lüfter {i + 1}: {ex.Message}"); _dev.Dispose(); _dev = null; return; }
            }
        }
        if (resend) _lastResend = DateTime.UtcNow;
    }

    // ---------- Stillstandsschutz ----------
    public const int KickPercent = 60;       // Anlaufhilfe, wenn ein angesteuerter Lüfter steht
    private readonly int[] _stallTicks = new int[3];
    private readonly int[] _kickTicks = new int[3];
    private readonly int[] _stallFloor = new int[3];   // gelernter Mindestwert je Kanal (gilt bis zum Programmende)
    private DateTime _connectedAt = DateTime.MinValue;

    /// Lüfter, der auch mit Anlaufhilfe keine Drehzahl meldet (blockiert, defekt oder ohne Drehzahlsignal).
    public bool[] Stalled { get; } = new bool[3];
    /// Tatsächlich gesendeter Wert je Kanal (kann wegen Anlaufhilfe/Mindestwert über dem Kurvenwert liegen).
    public int[] SentPercent => _sent.Select(s => Math.Max(0, s)).ToArray();
    public event Action<int>? FanStalled;

    /// Erkennt einen stehenden Lüfter (0 U/min trotz Ansteuerung, z. B. Gleichstromlüfter bei zu kleinem Wert):
    /// gibt ihm für einige Sekunden 60 % zum Anlaufen und hebt den Mindestwert des Kanals etwas an.
    /// 0 % aus der Kurve bleibt 0 % (gewollter Lüfterstopp). Läuft er auch mit 60 % nicht an, wird einmal gewarnt.
    private int StallGuard(int i, int d)
    {
        var f = _dev!.Fans[i];
        bool settling = DateTime.UtcNow - _connectedAt < TimeSpan.FromSeconds(15);   // Lüftererkennung nach dem Verbinden abwarten
        if (f.Mode == 0 || d <= 0 || settling)
        {
            _stallTicks[i] = 0; _kickTicks[i] = 0;
            if (!settling) Stalled[i] = false;
            return d;
        }
        if (d < _stallFloor[i]) d = _stallFloor[i];
        if (_kickTicks[i] > 0)
        {
            if (--_kickTicks[i] == 0)
            {
                _stallTicks[i] = 0;
                if (f.Rpm > 0) Stalled[i] = false;
                else if (!Stalled[i])
                {
                    Stalled[i] = true;
                    Log($"Lüfter {i + 1} meldet auch mit {KickPercent} % keine Drehzahl.");
                    FanStalled?.Invoke(i);
                }
            }
            return Math.Max(d, KickPercent);
        }
        if (f.Rpm > 0) { _stallTicks[i] = 0; Stalled[i] = false; return d; }
        if (Stalled[i]) return d;   // schon gemeldet: nicht dauernd erneut anschieben
        if (++_stallTicks[i] < 6) return d;
        _stallTicks[i] = 0; _kickTicks[i] = 5;
        _stallFloor[i] = Math.Min(KickPercent, Math.Max(_stallFloor[i], d + 5));
        Log($"Lüfter {i + 1} steht bei {d} %: Anlaufhilfe mit {KickPercent} %, Mindestwert jetzt {_stallFloor[i]} %.");
        return Math.Max(d, KickPercent);
    }

    /// Mindestdrehzahl bei hoher Temperatur, egal wie die Kurve aussieht:
    /// unter 80 °C keine, dann linear von 30 % (80 °C) bis 100 % (95 °C). Ungeglättete Temperatur, damit sie sofort greift.
    public static double SafetyFloor(double temp) => temp < 80 ? 0 : Math.Min(100, 30 + (temp - 80) / 15 * 70);

    private readonly HashSet<string> _logged = new();
    private void LogOnce(string msg) { if (_logged.Add(msg)) Log(msg); }

    public void Dispose()
    {
        if (_dev != null)
        {
            for (int i = 0; i < 3; i++) { try { _dev.SetDuty(i, ExitPercent); } catch { } }   // jeden Kanal einzeln, ein Fehler darf die anderen nicht verhindern
            Thread.Sleep(100);
        }
        _dev?.Dispose();
        Log("Beendet, Lüfter auf " + ExitPercent + " % gesetzt.");
    }
}
