using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NzxtFan;

public enum TempSource { CPU, GPU }

/// Eine Kurve ist nur die Form: Lüfter-% bei 20, 25, …, 100 °C.
public sealed class CurveDef
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int[] Percent { get; set; } = Curve.DefaultPercent.ToArray();
}

/// Abstimmung eines Lüfters innerhalb eines Profils: welche Kurve, nach welcher Temperatur.
public sealed class FanAssign
{
    public Guid CurveId { get; set; }
    public TempSource Source { get; set; } = TempSource.CPU;
    public FanAssign Clone() => new() { CurveId = CurveId, Source = Source };
    public bool SameAs(FanAssign o) => CurveId == o.CurveId && Source == o.Source;
}

/// Ein Profil speichert die gesamte Abstimmung aller Lüfter.
public sealed class Profile
{
    public string Name { get; set; } = "";
    public FanAssign[] Fans { get; set; } = { new(), new(), new() };
    /// Beleuchtung je Gerät/Zone (Teil der Abstimmung).
    public List<LightSetting> Lights { get; set; } = new();
}

public sealed class AppConfig
{
    public int Version { get; set; } = 2;
    public string[] FanNames { get; set; } = { "", "", "" };
    public int[] FanOrder { get; set; } = { 0, 1, 2 };          // Reihenfolge der Kacheln
    public string ActiveProfile { get; set; } = "";
    public List<Profile> Profiles { get; set; } = new();
    public List<CurveDef> Curves { get; set; } = new();
    public bool DarkTheme { get; set; } = true;
    public string Language { get; set; } = "de";
    public bool MinimizeToTrayOnly { get; set; } = true;

    // Geräte
    public bool FansEnabled { get; set; } = true;               // NZXT-Lüfter steuern
    public bool LightingEnabled { get; set; } = true;           // Beleuchtung über OpenRGB
    public List<string> LightDevices { get; set; } = new();     // eingeschaltete Beleuchtungsgeräte (Name laut OpenRGB)
    public string OpenRgbPath { get; set; } = "";
    public bool LightsImported { get; set; }

    // Akzentfarbe der Oberfläche
    public bool AccentFromLight { get; set; }
    public string AccentColor { get; set; } = "#8B5CF6";
    public string AccentDevice { get; set; } = CamLightImport.NzxtDevice;
    public int AccentZone { get; set; }

    // Farben von Lüfterrädern und Kurve in der Oberfläche
    public UiColorMode RotorMode { get; set; } = UiColorMode.Accent;      // alt (eine Einstellung für alle), nur noch zum Übernehmen
    public UiColorMode[]? RotorModes { get; set; }                         // je Lüfter
    public string[] RotorColors { get; set; } = { "#8B5CF6", "#8B5CF6", "#8B5CF6" };
    public string[] RotorZones { get; set; } = { "NZXT Smart Device V2|0", "NZXT Smart Device V2|0", "NZXT Smart Device V2|0" };
    public UiColorMode CurveMode { get; set; } = UiColorMode.Accent;
    public string CurveColor { get; set; } = "#8B5CF6";
    public string CurveZone { get; set; } = "NZXT Smart Device V2|0";
    public int RainbowSpeed { get; set; } = 40;                  // Grad Farbton pro Sekunde
    public string? ImportedFrom { get; set; }

    public Profile Active => Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles[0];
    public CurveDef? CurveById(Guid id) => Curves.FirstOrDefault(c => c.Id == id);
    public string FanName(int ch) => string.IsNullOrWhiteSpace(FanNames[ch]) ? Loc.T("Fan", ch + 1) : FanNames[ch];

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NzxtFan");
    public static string FilePath => Path.Combine(Dir, "config.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static AppConfig Load()
    {
        AppConfig? cfg = null;
        if (File.Exists(FilePath))
        {
            try
            {
                var text = File.ReadAllText(FilePath);
                var node = JsonNode.Parse(text) ?? throw new InvalidDataException("leere Datei");
                if (node["Version"] == null)
                {
                    File.Copy(FilePath, FilePath + ".v1.bak", true);      // Sicherung vor der Umstellung
                    cfg = MigrateV1(node);
                }
                else cfg = JsonSerializer.Deserialize<AppConfig>(text, Json);
            }
            catch (Exception ex)
            {
                // Unlesbare Datei nie stillschweigend überschreiben: erst wegsichern
                var bad = FilePath + $".defekt-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Copy(FilePath, bad, true); } catch { }
                FanController.Log($"Konfiguration unlesbar ({ex.Message}), gesichert als {bad}, baue neu auf.");
                cfg = null;
            }
        }

        if (cfg == null)
        {
            cfg = new AppConfig { Language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en" };
            Loc.Lang = cfg.Language;
            cfg.AddPresets();
            try { CamImport.Into(cfg); } catch (Exception ex) { FanController.Log("NZXT-Import fehlgeschlagen: " + ex.Message); }
        }
        cfg.Repair();
        Loc.Lang = cfg.Language;
        cfg.Save();
        return cfg;
    }

    /// Sorgt für ein gültiges Modell: gültige Kurven, eindeutige Namen, mindestens ein Profil, nur existierende Kurven.
    private void Repair()
    {
        if (Language is not ("de" or "en")) Language = "de";
        Loc.Lang = Language;
        Curves = (Curves ?? new()).Where(c => c != null).ToList();
        foreach (var c in Curves)
        {
            // Kurven immer mit 17 Werten zwischen 0 und 100, sonst Standard
            c.Percent = c.Percent is { Length: 17 } p ? p.Select(v => Math.Clamp(v, 0, 100)).ToArray() : Curve.DefaultPercent.ToArray();
            if (string.IsNullOrWhiteSpace(c.Name)) c.Name = Loc.T("Curve");
            if (c.Id == Guid.Empty) c.Id = Guid.NewGuid();
        }
        MakeUnique(Curves, c => c.Name, (c, n) => c.Name = n);
        if (Curves.Count == 0) AddPresets();

        Profiles = (Profiles ?? new()).Where(p => p != null).ToList();
        if (Profiles.Count == 0)
        {
            var std = Curves[0].Id;
            Profiles.Add(new Profile { Name = Loc.T("PresetStandard"), Fans = Enumerable.Range(0, 3).Select(_ => new FanAssign { CurveId = std }).ToArray() });
        }
        foreach (var p in Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Loc.T("Profile");
            var fans = p.Fans ?? Array.Empty<FanAssign>();
            p.Fans = Enumerable.Range(0, 3).Select(i => i < fans.Length && fans[i] != null ? fans[i] : new FanAssign()).ToArray();
            foreach (var f in p.Fans) if (CurveById(f.CurveId) == null) f.CurveId = Curves[0].Id;
        }
        MakeUnique(Profiles, p => p.Name, (p, n) => p.Name = n);
        if (Profiles.All(p => p.Name != ActiveProfile)) ActiveProfile = Profiles[0].Name;
        if (FanNames is not { Length: 3 }) FanNames = new[] { "", "", "" };
        FanNames = FanNames.Select(n => n ?? "").ToArray();
        foreach (var p in Profiles) p.Lights = (p.Lights ?? new()).Where(l => l != null && !string.IsNullOrEmpty(l.Device)).ToList();
        LightDevices ??= new();
        if (!LightsImported)
        {
            // Einmalig: NZXT-Beleuchtung aus NZXT CAM in alle Profile übernehmen
            List<LightSetting> cam;
            try { cam = CamLightImport.Read(); } catch (Exception ex) { FanController.Log("NZXT-Licht-Import: " + ex.Message); cam = new(); }
            if (cam.Count > 0)
            {
                foreach (var p in Profiles) if (p.Lights.Count == 0) p.Lights = cam.Select(l => l.Clone()).ToList();
                if (!LightDevices.Contains(CamLightImport.NzxtDevice)) LightDevices.Add(CamLightImport.NzxtDevice);
                FanController.Log($"Beleuchtung aus NZXT CAM übernommen ({cam.Count} Zonen).");
            }
            LightsImported = true;
        }
        if (RotorModes is not { Length: 3 }) RotorModes = new[] { RotorMode, RotorMode, RotorMode };
        if (RotorColors is not { Length: 3 }) RotorColors = new[] { AccentColor, AccentColor, AccentColor };
        if (RotorZones is not { Length: 3 }) RotorZones = Enumerable.Repeat(UiColors.ZoneKey(CamLightImport.NzxtDevice, 0), 3).ToArray();
        RainbowSpeed = Math.Clamp(RainbowSpeed, 5, 180);
        if (FanOrder is not { Length: 3 } || FanOrder.Distinct().Count() != 3 || FanOrder.Any(i => i is < 0 or > 2)) FanOrder = new[] { 0, 1, 2 };
    }

    private static void MakeUnique<T>(List<T> items, Func<T, string> get, Action<T, string> set)
    {
        var seen = new HashSet<string>();
        foreach (var it in items)
        {
            var n = get(it); var baseName = n; int k = 2;
            while (!seen.Add(n)) n = $"{baseName} ({k++})";
            if (n != get(it)) set(it, n);
        }
    }

    internal void AddPresets()
    {
        Curves.Add(new CurveDef { Name = Loc.T("PresetStandard"), Percent = Curve.DefaultPercent.ToArray() });
        Curves.Add(new CurveDef { Name = Loc.T("PresetSilent"), Percent = new[] { 0, 0, 0, 0, 0, 0, 0, 15, 25, 35, 50, 65, 80, 90, 100, 100, 100 } });
        Curves.Add(new CurveDef { Name = Loc.T("PresetPerformance"), Percent = new[] { 35, 35, 35, 40, 45, 50, 55, 65, 75, 85, 95, 100, 100, 100, 100, 100, 100 } });
        Curves.Add(new CurveDef { Name = Loc.T("PresetFixed50"), Percent = Enumerable.Repeat(50, 17).ToArray() });
    }

    /// Gleiche Form wiederverwenden, sonst neue Kurve anlegen.
    public Guid AddOrFindCurve(string name, int[] percent)
    {
        var same = Curves.FirstOrDefault(c => c.Percent.SequenceEqual(percent));
        if (same != null) return same.Id;
        var n = name; int k = 2;
        while (Curves.Any(c => c.Name == n)) n = $"{name} ({k++})";
        var c = new CurveDef { Name = n, Percent = percent.ToArray() };
        Curves.Add(c);
        return c.Id;
    }

    /// Altes Format (Version 1: Kurven direkt im Profil) ins neue Modell überführen.
    private static AppConfig MigrateV1(JsonNode n)
    {
        var cfg = new AppConfig
        {
            DarkTheme = n["DarkTheme"]?.GetValue<bool>() ?? true,
            Language = n["Language"]?.GetValue<string>() ?? "de",
            ActiveProfile = n["ActiveProfile"]?.GetValue<string>() ?? "",
            ImportedFrom = n["ImportedFrom"]?.GetValue<string>()
        };
        Loc.Lang = cfg.Language;
        var names = n["FanNames"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").ToArray();
        if (names is { Length: 3 }) cfg.FanNames = names.Select(s => s.StartsWith("Lüfter ") ? "" : s).ToArray();
        cfg.AddPresets();
        foreach (var sc in n["SavedCurves"]?.AsArray() ?? new JsonArray())
        {
            var pc = sc?["Percent"]?.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            if (pc is { Length: 17 }) cfg.AddOrFindCurve(sc!["Name"]?.GetValue<string>() ?? Loc.T("Curve"), pc);
        }
        foreach (var p in n["Profiles"]?.AsArray() ?? new JsonArray())
        {
            var prof = new Profile { Name = p?["Name"]?.GetValue<string>() ?? Loc.T("Profile") };
            var chs = p?["Channels"]?.AsArray();
            for (int i = 0; i < 3 && chs != null && i < chs.Count; i++)
            {
                var pc = chs[i]?["Percent"]?.AsArray().Select(x => x!.GetValue<int>()).ToArray() ?? Curve.DefaultPercent;
                prof.Fans[i] = new FanAssign
                {
                    CurveId = cfg.AddOrFindCurve($"{prof.Name} · {cfg.FanName(i)}", pc),
                    Source = chs[i]?["Source"]?.GetValue<string>() == "GPU" ? TempSource.GPU : TempSource.CPU
                };
            }
            cfg.Profiles.Add(prof);
        }
        FanController.Log("Konfiguration auf Version 2 umgestellt (Kurven und Profile getrennt).");
        return cfg;
    }

    /// Wird ausgelöst, wenn das Speichern scheitert (z. B. Datei gesperrt), damit die Oberfläche warnen kann.
    public static event Action<string>? SaveFailed;

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
            return true;
        }
        catch (Exception ex)
        {
            FanController.Log("Speichern fehlgeschlagen: " + ex.Message);
            SaveFailed?.Invoke(ex.Message);
            return false;
        }
    }
}

public static class Curve
{
    /// Temperaturstufen wie in NZXT CAM: 20, 25, …, 100 °C.
    public static readonly int[] Temps = Enumerable.Range(0, 17).Select(i => 20 + i * 5).ToArray();
    public static readonly int[] DefaultPercent = { 20, 20, 20, 20, 20, 25, 30, 35, 45, 55, 65, 75, 85, 95, 100, 100, 100 };

    public static double Evaluate(int[] percent, double temp)
    {
        if (temp <= Temps[0]) return percent[0];
        if (temp >= Temps[^1]) return percent[^1];
        int i = (int)((temp - Temps[0]) / 5);
        double f = (temp - Temps[i]) / 5.0;
        return percent[i] + (percent[i + 1] - percent[i]) * f;
    }
}

/// Übernimmt Lüfterprofile und Lüfternamen aus der Datensicherung von NZXT CAM.
public static class CamImport
{
    public static string? FindBackup()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NZXT CAM", "DataStorage");
        if (!Directory.Exists(root)) return null;
        return Directory.GetFiles(root, "*_backup.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public static void Into(AppConfig cfg)
    {
        var file = FindBackup();
        if (file == null) return;
        var cc = JsonNode.Parse(File.ReadAllText(file))?["user"]?["coolingController"];
        if (cc == null) return;

        foreach (var ch in cc["channelSettings"]?.AsArray() ?? new JsonArray())
        {
            if (ch?["deviceIdentifier"]?["uniqueId"]?.GetValue<string>()?.StartsWith("smartdevice2") != true) continue;
            int idx = ChannelIndex(ch["channelName"]?.GetValue<string>());
            var nick = ch["nickname"]?.GetValue<string>();
            if (idx >= 0 && !string.IsNullOrWhiteSpace(nick)) cfg.FanNames[idx] = nick!;
        }

        if (cfg.Curves.Count == 0) cfg.Curves.Add(new CurveDef { Name = Loc.T("PresetStandard"), Percent = Curve.DefaultPercent.ToArray() });
        string? activeId = cc["activeProfileId"]?.GetValue<string>();
        foreach (var p in cc["profiles"]?.AsArray() ?? new JsonArray())
        {
            if (p == null) continue;
            var prof = new Profile { Name = p["name"]?.GetValue<string>() is { Length: > 0 } n ? n : "NZXT Standard" };
            for (int i = 0; i < 3; i++) prof.Fans[i] = new FanAssign { CurveId = cfg.Curves[0].Id };
            bool any = false;
            foreach (var ch in p["channelSettings"]?.AsArray() ?? new JsonArray())
            {
                if (ch?["deviceIdentifier"]?["uniqueId"]?.GetValue<string>()?.StartsWith("smartdevice2") != true) continue;
                int idx = ChannelIndex(ch["channelName"]?.GetValue<string>());
                var mode = ch["mode"];
                if (idx < 0 || mode?["type"]?.GetValue<string>() != "Custom") continue;
                var pts = mode["customThresholds"]?.AsArray();
                if (pts == null || pts.Count == 0) continue;
                // doppelte Temperaturen tolerieren; fehlende Stufen vom nächsten bekannten Punkt übernehmen (keine 100-%-Spitzen)
                var known = pts.Where(x => x?["temperature"] != null && x["fanPercentage"] != null)
                    .GroupBy(x => x!["temperature"]!.GetValue<int>())
                    .ToDictionary(g => g.Key, g => Math.Clamp(g.Last()!["fanPercentage"]!.GetValue<int>(), 0, 100));
                if (known.Count == 0) continue;
                var percent = Curve.Temps.Select(t => known.TryGetValue(t, out var v) ? v : known.MinBy(k => Math.Abs(k.Key - t)).Value).ToArray();
                prof.Fans[idx] = new FanAssign
                {
                    CurveId = cfg.AddOrFindCurve($"{prof.Name} · {cfg.FanName(idx)}", percent),
                    Source = mode["temperatureOption"]?.GetValue<string>() == "GPU" ? TempSource.GPU : TempSource.CPU
                };
                any = true;
            }
            if (!any) continue;
            while (cfg.Profiles.Any(x => x.Name == prof.Name)) prof.Name += " (2)";
            cfg.Profiles.Add(prof);
            if (p["id"]?.GetValue<string>() == activeId) cfg.ActiveProfile = prof.Name;
        }
        cfg.ImportedFrom = file;
    }

    private static int ChannelIndex(string? name) => name switch { "channel 1" => 0, "channel 2" => 1, "channel 3" => 2, _ => -1 };
}
