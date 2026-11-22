using System.Windows;
using System.Windows.Media;

namespace NzxtFan;

/// Hell/Dunkel: tauscht die Farbpinsel in den App-Ressourcen aus (alle Stellen nutzen DynamicResource).
public static class Theme
{
    public static bool Dark { get; private set; } = true;

    public static void Apply(bool dark)
    {
        Dark = dark;
        var r = Application.Current.Resources;
        void Set(string key, string hex) => r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        if (dark)
        {
            Set("Bg", "#0F1116"); Set("Card", "#1A1D25"); Set("CardHover", "#232733"); Set("Line", "#2A2F3C");
            Set("Text", "#ECEEF3"); Set("Muted", "#8A90A2");
            Set("Warn", "#F59E0B"); Set("WarnBg", "#3A2A0A"); Set("Hub", "#2A2E3A");
            r["CardGradient"] = Gradient("#222632", "#171A21");
            r["ShadowColor"] = Colors.Black; r["ShadowOpacity"] = 0.55;
        }
        else
        {
            Set("Bg", "#E9ECF2"); Set("Card", "#FFFFFF"); Set("CardHover", "#F2F3F8"); Set("Line", "#DADFE8");
            Set("Text", "#171A21"); Set("Muted", "#646B7B");
            Set("Warn", "#B45309"); Set("WarnBg", "#FEF3C7"); Set("Hub", "#E3E6EE");
            r["CardGradient"] = Gradient("#FFFFFF", "#F3F4F8");
            r["ShadowColor"] = Color.FromRgb(0x4B, 0x55, 0x63); r["ShadowOpacity"] = 0.22;
        }
        SetAccent(Accent);
    }

    /// Aktuelle Akzentfarbe (frei wählbar oder von der Gehäusebeleuchtung übernommen).
    public static Color Accent { get; private set; } = Color.FromRgb(0x8B, 0x5C, 0xF6);

    /// Setzt alle Akzent-Pinsel. Lüfterräder, Kurve, Balken und Knöpfe beziehen sie per DynamicResource und ändern sich sofort mit.
    public static void SetAccent(Color c)
    {
        c = Visible(c);
        Accent = c;
        var r = Application.Current.Resources;
        var dark = Mix(c, Colors.Black, 0.45);
        var light = Mix(c, Colors.White, 0.55);
        r["Accent"] = Freeze(new SolidColorBrush(c));
        r["AccentDark"] = Freeze(new SolidColorBrush(dark));
        r["AccentBlade"] = Freeze(new LinearGradientBrush(c, dark, 60));
        r["AccentBar"] = Freeze(new LinearGradientBrush(dark, c, 0));
        r["AccentArea"] = Freeze(new LinearGradientBrush(Color.FromArgb(110, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90));
        r["AccentHub"] = Freeze(new RadialGradientBrush(light, dark) { GradientOrigin = new Point(0.3, 0.25), Center = new Point(0.4, 0.35), RadiusX = 0.7, RadiusY = 0.7 });
        r["AccentColor"] = c;
    }

    /// Sehr dunkle Farben (z. B. LEDs aus) würden in der Oberfläche unsichtbar: Mindesthelligkeit sicherstellen.
    public static Color Visible(Color c)
    {
        double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        return lum < 60 ? Mix(c, Colors.White, (60 - lum) / 255.0 * 1.5) : c;
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// Färbt die Windows-Titelleiste passend zum Theme (Windows 11; unter Windows 10 nur hell/dunkel).
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = ToColorRef(C("Bg")), text = ToColorRef(C("Text"));
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));       // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));          // DWMWA_TEXT_COLOR
        int border = ToColorRef(C("Bg"));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));        // DWMWA_BORDER_COLOR
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private static LinearGradientBrush Gradient(string top, string bottom) =>
        new((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);

    public static Brush B(string key) => (Brush)Application.Current.Resources[key];
    public static Color C(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    /// Weicher Schatten unter Karten – gibt den Kacheln Tiefe.
    public static System.Windows.Media.Effects.DropShadowEffect Shadow(double depth = 6, double blur = 22) => new()
    {
        Color = (Color)Application.Current.Resources["ShadowColor"],
        Opacity = (double)Application.Current.Resources["ShadowOpacity"],
        ShadowDepth = depth, BlurRadius = blur, Direction = 270
    };
}
