using System.Windows;
using System.Windows.Media;

namespace NzxtFan;

/// Wie Lüfterräder und Kurve in der Oberfläche gefärbt werden.
public enum UiColorMode { Accent, Fixed, Light, Rainbow }

/// Färbt Lüfterräder (je Lüfter) und Kurve. Wird bei jedem Bild aufgerufen, damit Regenbogen und Lichtfarben flüssig laufen.
public static class UiColors
{
    private static readonly Color?[] _lastRotor = new Color?[3];
    private static Color? _lastCurve;
    private static string _lastCurveKind = "";

    /// Alle Pinsel einmal neu setzen (z. B. nach Theme- oder Einstellungswechsel).
    public static void Invalidate() { Array.Clear(_lastRotor); _lastCurve = null; _lastCurveKind = ""; }

    public static void Update(AppConfig cfg, LightingEngine light, double seconds)
    {
        for (int ch = 0; ch < 3; ch++)
        {
            var mode = cfg.RotorModes![ch];
            if (mode == UiColorMode.Rainbow)
            {
                // Welle: jeder Lüfter um 120° versetzt
                double hue = (seconds * cfg.RainbowSpeed + ch * 120) % 360;
                SetRotor(ch, Hsv(hue), Hsv(hue + 50));
                _lastRotor[ch] = null;
                continue;
            }
            var c = mode switch
            {
                UiColorMode.Fixed => Rgb.ToMedia(cfg.RotorColors[ch]),
                UiColorMode.Light => LightColor(light, cfg.RotorZones[ch]) ?? Theme.Accent,
                _ => Theme.Accent
            };
            if (_lastRotor[ch] == c) continue;
            _lastRotor[ch] = c;
            var v = Theme.Visible(c);
            SetRotor(ch, v, Theme.Mix(v, Colors.Black, 0.45));
        }

        if (cfg.CurveMode == UiColorMode.Rainbow)
        {
            SetCurveRainbow(seconds * cfg.RainbowSpeed);
            _lastCurveKind = "rainbow";
        }
        else
        {
            var c = cfg.CurveMode switch
            {
                UiColorMode.Fixed => Rgb.ToMedia(cfg.CurveColor),
                UiColorMode.Light => LightColor(light, cfg.CurveZone) ?? Theme.Accent,
                _ => Theme.Accent
            };
            if (_lastCurveKind != "solid" || _lastCurve != c)
            {
                _lastCurve = c; _lastCurveKind = "solid";
                var v = Theme.Visible(c);
                var r = Application.Current.Resources;
                r["CurveStroke"] = F(new SolidColorBrush(v));
                r["CurveArea"] = F(new LinearGradientBrush(Color.FromArgb(110, v.R, v.G, v.B), Color.FromArgb(0, v.R, v.G, v.B), 90));
                CurveGlow = v;
            }
        }
    }

    /// Farbe, die der Leuchteffekt der Kurve annehmen soll.
    public static Color CurveGlow { get; private set; } = Colors.MediumPurple;

    private static Color? LightColor(LightingEngine light, string zoneKey)
    {
        var (dev, zone) = ParseZone(zoneKey);
        return light.ShownColor(dev, zone) is { } c ? Color.FromRgb(c.r, c.g, c.b) : null;
    }

    public static string ZoneKey(string dev, int zone) => $"{dev}|{zone}";
    public static (string dev, int zone) ParseZone(string key)
    {
        var i = key.LastIndexOf('|');
        return i < 0 ? (key, 0) : (key[..i], int.TryParse(key[(i + 1)..], out var z) ? z : 0);
    }

    private static void SetRotor(int ch, Color c, Color dark)
    {
        var r = Application.Current.Resources;
        var light = Theme.Mix(c, Colors.White, 0.55);
        r[$"RotorBlade{ch}"] = F(new LinearGradientBrush(c, dark, 60));
        r[$"RotorHub{ch}"] = F(new RadialGradientBrush(light, dark) { GradientOrigin = new Point(0.3, 0.25), Center = new Point(0.4, 0.35), RadiusX = 0.7, RadiusY = 0.7 });
        r[$"RotorBar{ch}"] = F(new LinearGradientBrush(dark, c, 0));
    }

    private static void SetCurveRainbow(double hueShift)
    {
        var stroke = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var area = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++)
        {
            var c = Hsv(hueShift + i * 60);
            stroke.GradientStops.Add(new GradientStop(c, i / 6.0));
            area.GradientStops.Add(new GradientStop(Color.FromArgb(70, c.R, c.G, c.B), i / 6.0));
        }
        var r = Application.Current.Resources;
        r["CurveStroke"] = F(stroke); r["CurveArea"] = F(area);
        CurveGlow = Hsv(hueShift + 180);
    }

    public static Color Hsv(double h)
    {
        h = ((h % 360) + 360) % 360;
        double x = 1 - Math.Abs(h / 60 % 2 - 1);
        (double r, double g, double b) = h < 60 ? (1, x, 0d) : h < 120 ? (x, 1, 0d) : h < 180 ? (0d, 1, x) : h < 240 ? (0d, x, 1d) : h < 300 ? (x, 0d, 1d) : (1d, 0d, x);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static T F<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
