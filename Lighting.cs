using System.IO;
using System.Text.Json.Nodes;

namespace NzxtFan;

public enum LightMode { Off, Static, Temperature, Effect }

/// Farbpunkt im Temperaturverlauf.
public sealed class GradStop
{
    public int Temp { get; set; }
    public string Color { get; set; } = "#00FF00";
    public GradStop Clone() => new() { Temp = Temp, Color = Color };
}

/// Beleuchtung einer Zone (Zone ≥ 0) oder eines ganzen Geräts (Zone = -1, für Effekte).
public sealed class LightSetting
{
    public string Device { get; set; } = "";
    public int Zone { get; set; } = -1;
    public LightMode Mode { get; set; } = LightMode.Static;
    public string Color { get; set; } = "#8B5CF6";
    public TempSource Source { get; set; } = TempSource.CPU;
    public List<GradStop> Stops { get; set; } = DefaultStops();
    public string Effect { get; set; } = "";
    public List<string> EffectColors { get; set; } = new() { "#8B5CF6" };
    public int Speed { get; set; } = 50;          // 0–100 % des Gerätebereichs
    public int Brightness { get; set; } = 100;    // 0–100 %

    public static List<GradStop> DefaultStops() => new()
    {
        new() { Temp = 40, Color = "#00FF00" }, new() { Temp = 60, Color = "#BAFF00" }, new() { Temp = 70, Color = "#FFFF00" },
        new() { Temp = 80, Color = "#FF4800" }, new() { Temp = 90, Color = "#FF0000" }
    };

    public LightSetting Clone() => new()
    {
        Device = Device, Zone = Zone, Mode = Mode, Color = Color, Source = Source, Stops = Stops.Select(s => s.Clone()).ToList(),
        Effect = Effect, EffectColors = EffectColors.ToList(), Speed = Speed, Brightness = Brightness
    };

    /// Vergleich über den Inhalt (für „Profil geändert“).
    public string Signature => $"{Device}|{Zone}|{Mode}|{Color}|{Source}|{string.Join(",", Stops.Select(s => s.Temp + s.Color))}|{Effect}|{string.Join(",", EffectColors)}|{Speed}|{Brightness}";
}

public static class Rgb
{
    public static (byte r, byte g, byte b) Parse(string hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return (0, 0, 0);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static System.Windows.Media.Color ToMedia(string hex) { var (r, g, b) = Parse(hex); return System.Windows.Media.Color.FromRgb(r, g, b); }

    /// Farbe im Temperaturverlauf, linear zwischen den Punkten.
    public static (byte r, byte g, byte b) Gradient(IReadOnlyList<GradStop> stops, double temp)
    {
        if (stops.Count == 0) return (0, 0, 0);
        var s = stops.OrderBy(x => x.Temp).ToList();
        if (temp <= s[0].Temp) return Parse(s[0].Color);
        if (temp >= s[^1].Temp) return Parse(s[^1].Color);
        for (int i = 0; i < s.Count - 1; i++)
        {
            if (temp > s[i + 1].Temp) continue;
            double f = (temp - s[i].Temp) / Math.Max(1, s[i + 1].Temp - s[i].Temp);
            var a = Parse(s[i].Color); var b = Parse(s[i + 1].Color);
            return ((byte)(a.r + (b.r - a.r) * f), (byte)(a.g + (b.g - a.g) * f), (byte)(a.b + (b.b - a.b) * f));
        }
        return Parse(s[^1].Color);
    }

    public static (byte r, byte g, byte b) Dim((byte r, byte g, byte b) c, int brightness)
    {
        double k = Math.Clamp(brightness, 0, 100) / 100.0;
        return ((byte)(c.r * k), (byte)(c.g * k), (byte)(c.b * k));
    }
}

/// Übernimmt die Beleuchtung der NZXT-Kanäle aus dem aktiven Beleuchtungsprofil von NZXT CAM.
public static class CamLightImport
{
    public const string NzxtDevice = "NZXT Smart Device V2";

    public static List<LightSetting> Read()
    {
        var result = new List<LightSetting>();
        var file = CamImport.FindBackup();
        if (file == null) return result;
        var lc = JsonNode.Parse(File.ReadAllText(file))?["user"]?["lightingController"];
        var active = lc?["activeProfileId"]?.GetValue<string>();
        var prof = lc?["profiles"]?.AsArray().FirstOrDefault(p => p?["id"]?.GetValue<string>() == active);
        if (prof == null) return result;
        double global = prof["globalBrightness"]?.GetValue<double>() ?? 1;
        foreach (var ch in prof["channelSettings"]?.AsArray() ?? new JsonArray())
        {
            if (ch?["deviceUniqueId"]?.GetValue<string>()?.StartsWith("smartdevice2") != true) continue;
            int zone = ch["channelName"]?.GetValue<string>() switch { "channel 1" => 0, "channel 2" => 1, _ => -1 };
            if (zone < 0) continue;
            var mode = ch["mode"];
            var s = new LightSetting
            {
                Device = NzxtDevice, Zone = zone,
                Brightness = (int)Math.Round((ch["brightness"]?.GetValue<double>() ?? 1) * global * 100)
            };
            var name = mode?["modeName"]?.GetValue<string>() ?? "";
            if (name is "CPUTemperature" or "GPUTemperature")
            {
                s.Mode = LightMode.Temperature;
                s.Source = name == "GPUTemperature" ? TempSource.GPU : TempSource.CPU;
                s.Stops = (mode!["colorsWithNumbers"]?.AsArray() ?? new JsonArray())
                    .Where(x => x != null).Select(x => new GradStop { Temp = x!["value"]!.GetValue<int>(), Color = "#" + x["ledRgb"]!.GetValue<string>() }).ToList();
                if (s.Stops.Count == 0) s.Stops = LightSetting.DefaultStops();
            }
            else if (mode?["ledRgbs"]?.AsArray() is { Count: > 0 } rgbs)
            {
                s.Mode = LightMode.Static; s.Color = "#" + rgbs[0]!.GetValue<string>();
            }
            else continue;
            result.Add(s);
        }
        return result;
    }
}
