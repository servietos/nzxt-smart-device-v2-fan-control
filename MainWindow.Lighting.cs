using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NzxtFan;

/// Beleuchtung, Farbwähler, Geräte- und Akzentfarben-Einstellungen.
public partial class MainWindow
{
    private List<LightSetting> _workLights = new();
    private readonly Dictionary<(string dev, int zone), Ellipse> _zoneDots = new();
    private bool LightsDirty => Sig(_workLights) != Sig(Cfg.Active.Lights);
    private static string Sig(IEnumerable<LightSetting> l) => string.Join(";", l.OrderBy(x => x.Device).ThenBy(x => x.Zone).Select(x => x.Signature));

    // ===================== Bereich umschalten =====================
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (FansPanel == null) return;
        bool light = TabLight.IsChecked == true;
        FansPanel.Visibility = light ? Visibility.Collapsed : Visibility.Visible;
        LightPanel.Visibility = light ? Visibility.Visible : Visibility.Collapsed;
        if (light) BuildLightView();
    }

    /// Arbeitsstand der Beleuchtung an den Hintergrunddienst geben (Live-Vorschau am Gehäuse).
    private void PushLights() => _app.Lighting.Configure(Cfg.LightingEnabled, Cfg.OpenRgbPath, Cfg.LightDevices, _workLights);

    private void LightsChanged(bool rebuild = false)
    {
        PushLights(); UpdateDirtyUi();   // UpdateDirtyUi merkt sich den Schritt auch für „Rückgängig“
        if (rebuild) BuildLightView();
    }

    private LightSetting GetLight(string dev, int zone)
    {
        var s = _workLights.FirstOrDefault(l => l.Device == dev && l.Zone == zone);
        if (s == null)
        {
            s = new LightSetting { Device = dev, Zone = zone, Mode = zone < 0 ? LightMode.Effect : LightMode.Static, Color = Rgb.Hex(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B) };
            _workLights.Add(s);
        }
        return s;
    }

    // ===================== Beleuchtungsansicht =====================
    private void BuildLightView()
    {
        LightContent.Children.Clear();
        _zoneDots.Clear();
        if (!Cfg.LightingEnabled || Cfg.LightDevices.Count == 0)
        {
            LightContent.Children.Add(Note(Loc.T("LightNoDevices")));
            return;
        }
        var status = Txt(13, "Muted"); status.Text = _app.Lighting.Status; status.Margin = new Thickness(0, 0, 0, 10);
        LightContent.Children.Add(status);
        var devices = _app.Lighting.Devices;
        foreach (var name in Cfg.LightDevices)
        {
            var d = devices.FirstOrDefault(x => x.Name == name);
            LightContent.Children.Add(d == null ? Card(name, Note(Loc.T("LightSearching"))) : DeviceCard(d));
        }
    }

    private TextBlock Note(string text) { var t = Txt(13, "Muted"); t.Text = text; t.TextWrapping = TextWrapping.Wrap; return t; }

    private Border Card(string title, UIElement body)
    {
        var sp = new StackPanel();
        var h = Txt(17, "Text", FontWeights.SemiBold); h.Text = title; h.Margin = new Thickness(0, 0, 0, 10);
        sp.Children.Add(h); sp.Children.Add(body);
        var b = new Border { Child = sp, CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 16, 20, 16), Margin = new Thickness(0, 0, 0, 16), Effect = Theme.Shadow() };
        b.SetResourceReference(Border.BackgroundProperty, "CardGradient");
        return b;
    }

    private Border DeviceCard(RgbDevice d)
    {
        var body = new StackPanel();
        var devSetting = _workLights.FirstOrDefault(l => l.Device == d.Name && l.Zone < 0);
        bool effect = devSetting?.Mode == LightMode.Effect;

        // Steuerung: Zonen einzeln oder ein Effekt für das ganze Gerät
        var ctl = Segmented(new[] { Loc.T("LightZones"), Loc.T("LightEffect") }, effect ? 1 : 0, i =>
        {
            if (i == 1) GetLight(d.Name, -1).Mode = LightMode.Effect;
            else _workLights.RemoveAll(l => l.Device == d.Name && l.Zone < 0);
            LightsChanged(rebuild: true);
        });
        body.Children.Add(Row(Loc.T("LightControl"), ctl));

        if (effect) body.Children.Add(EffectEditor(d, devSetting!));
        else
        {
            foreach (var z in d.Zones.Where(z => z.LedsCount > 0)) body.Children.Add(ZoneEditor(d, z));
            int hidden = d.Zones.Count(z => z.LedsCount == 0);
            if (hidden > 0) body.Children.Add(Note(Loc.T("LightHiddenZones", hidden)));
        }
        return Card(d.Name, body);
    }

    private UIElement EffectEditor(RgbDevice d, LightSetting s)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var modes = d.Modes.Where(m => !m.IsDirect).ToList();
        if (!modes.Any(m => m.Name == s.Effect) && modes.Count > 0) s.Effect = modes[0].Name;
        var mode = modes.FirstOrDefault(m => m.Name == s.Effect);

        var box = new ComboBox { Width = 240, ItemsSource = modes.Select(m => m.Name).ToList(), SelectedItem = s.Effect };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string n) { s.Effect = n; LightsChanged(rebuild: true); } };
        sp.Children.Add(Row(Loc.T("LightEffectName"), box));

        if (mode != null && mode.UsesModeColors)
        {
            int n = (int)Math.Clamp(mode.ColorsMax, 1, 8);
            while (s.EffectColors.Count < Math.Max(1, (int)mode.ColorsMin)) s.EffectColors.Add(s.EffectColors.LastOrDefault() ?? "#FFFFFF");
            var colors = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < Math.Min(n, s.EffectColors.Count); i++)
            {
                int idx = i;
                colors.Children.Add(Swatch(s.EffectColors[i], hex => { s.EffectColors[idx] = hex; LightsChanged(); }));
            }
            if (s.EffectColors.Count < n)
            {
                var add = new Button { Content = "+", Style = (Style)FindResource("Btn"), Width = 34, Height = 34, Padding = new Thickness(0) };
                add.Click += (_, _) => { s.EffectColors.Add(s.EffectColors.LastOrDefault() ?? "#FFFFFF"); LightsChanged(rebuild: true); };
                colors.Children.Add(add);
            }
            if (s.EffectColors.Count > Math.Max(1, (int)mode.ColorsMin))
            {
                var del = new Button { Content = "−", Style = (Style)FindResource("Btn"), Width = 34, Height = 34, Padding = new Thickness(0) };
                del.Click += (_, _) => { s.EffectColors.RemoveAt(s.EffectColors.Count - 1); LightsChanged(rebuild: true); };
                colors.Children.Add(del);
            }
            sp.Children.Add(Row(Loc.T("LightColors"), colors));
        }
        if (mode?.HasSpeed == true) sp.Children.Add(Row(Loc.T("LightSpeed"), PercentSlider(s.Speed, v => { s.Speed = v; LightsChanged(); })));
        sp.Children.Add(Row(Loc.T("LightBrightness"), PercentSlider(s.Brightness, v => { s.Brightness = v; LightsChanged(); })));
        return sp;
    }

    private UIElement ZoneEditor(RgbDevice d, RgbZone z)
    {
        var s = _workLights.FirstOrDefault(l => l.Device == d.Name && l.Zone == z.Index);
        var mode = s?.Mode ?? LightMode.Off;
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        // Kopf: Live-Farbpunkt + Zonenname + LED-Anzahl
        var dot = new Ellipse { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Stroke = Theme.B("Line"), StrokeThickness = 1 };
        _zoneDots[(d.Name, z.Index)] = dot;
        var title = Txt(14, "Text", FontWeights.SemiBold); title.Text = $"{z.Name}";
        var leds = Txt(12, "Muted"); leds.Text = $"  ·  {z.LedsCount} LEDs"; leds.VerticalAlignment = VerticalAlignment.Center;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(dot); head.Children.Add(title); head.Children.Add(leds);
        var fans = Cfg.FanOrder.Where(ch => Cfg.RotorZones[ch] == UiColors.ZoneKey(d.Name, z.Index)).Select(Cfg.FanName).ToList();
        var fanText = Txt(12, fans.Count > 0 ? "Text" : "Muted"); fanText.VerticalAlignment = VerticalAlignment.Center;
        fanText.Text = fans.Count > 0 ? "  ·  " + Loc.T("ZoneFans", string.Join(", ", fans)) : "  ·  " + Loc.T("ZoneNoFans");
        head.Children.Add(fanText);
        sp.Children.Add(head);

        var modeSel = Segmented(new[] { Loc.T("LightOffMode"), Loc.T("LightStatic"), Loc.T("LightTemp") }, (int)mode, i =>
        {
            var st = GetLight(d.Name, z.Index);
            st.Mode = (LightMode)i;
            LightsChanged(rebuild: true);
        });
        sp.Children.Add(Row(Loc.T("LightMode"), modeSel));

        if (s != null && mode == LightMode.Static)
            sp.Children.Add(Row(Loc.T("LightColor"), Swatch(s.Color, hex => { s.Color = hex; LightsChanged(); })));

        if (s != null && mode == LightMode.Temperature)
        {
            var src = Segmented(new[] { "CPU", "GPU" }, s.Source == TempSource.GPU ? 1 : 0, i => { s.Source = i == 1 ? TempSource.GPU : TempSource.CPU; LightsChanged(); });
            sp.Children.Add(Row(Loc.T("TempFrom"), src));
            sp.Children.Add(Row(Loc.T("LightGradient"), GradientEditor(s)));
        }
        if (s != null && mode != LightMode.Off)
        {
            sp.Children.Add(Row(Loc.T("LightBrightness"), PercentSlider(s.Brightness, v => { s.Brightness = v; LightsChanged(); })));
            var copy = new Button { Content = Loc.T("LightCopyAll"), Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0) };
            copy.Click += (_, _) =>
            {
                // Diese Zoneneinstellung auf alle Zonen aller eingeschalteten Geräte übertragen
                foreach (var dev in _app.Lighting.Devices.Where(x => Cfg.LightDevices.Contains(x.Name)))
                {
                    _workLights.RemoveAll(l => l.Device == dev.Name && l.Zone < 0);
                    foreach (var zz in dev.Zones.Where(q => q.LedsCount > 0))
                    {
                        if (dev.Name == d.Name && zz.Index == z.Index) continue;
                        _workLights.RemoveAll(l => l.Device == dev.Name && l.Zone == zz.Index);
                        var c = s.Clone(); c.Device = dev.Name; c.Zone = zz.Index; _workLights.Add(c);
                    }
                }
                LightsChanged(rebuild: true);
            };
            sp.Children.Add(Row("", copy));
        }
        return sp;
    }

    /// Temperaturverlauf: Vorschau-Balken (20–100 °C) und Punkte mit Temperatur und Farbe.
    private UIElement GradientEditor(LightSetting s)
    {
        var sp = new StackPanel();
        var stops = s.Stops.OrderBy(x => x.Temp).ToList();
        var bar = new Border { Height = 14, CornerRadius = new CornerRadius(7), Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
        var gb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (var st in stops) gb.GradientStops.Add(new GradientStop(Rgb.ToMedia(st.Color), Math.Clamp((st.Temp - 20) / 80.0, 0, 1)));
        bar.Background = gb;
        sp.Children.Add(bar);

        var list = new WrapPanel();
        foreach (var st in stops)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
            var tb = new TextBox { Text = st.Temp.ToString(), Width = 52, Height = 32 };
            void commit() { if (int.TryParse(tb.Text, out var v) && v != st.Temp) { st.Temp = Math.Clamp(v, 0, 110); LightsChanged(rebuild: true); } }
            tb.LostFocus += (_, _) => commit();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) commit(); };
            var unit = Txt(12, "Muted"); unit.Text = "°C"; unit.VerticalAlignment = VerticalAlignment.Center; unit.Margin = new Thickness(4, 0, 6, 0);
            item.Children.Add(tb); item.Children.Add(unit);
            item.Children.Add(Swatch(st.Color, hex => { st.Color = hex; LightsChanged(); }, small: true));
            if (s.Stops.Count > 2)
            {
                var x = new Button { Content = "×", Style = (Style)FindResource("Btn"), Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
                x.Click += (_, _) => { s.Stops.Remove(st); LightsChanged(rebuild: true); };
                item.Children.Add(x);
            }
            list.Children.Add(item);
        }
        var add = new Button { Content = Loc.T("LightAddPoint"), Style = (Style)FindResource("Btn"), Height = 32, Margin = new Thickness(0, 0, 0, 6) };
        add.Click += (_, _) =>
        {
            var last = s.Stops.OrderBy(x => x.Temp).LastOrDefault();
            s.Stops.Add(new GradStop { Temp = Math.Min(110, (last?.Temp ?? 50) + 5), Color = last?.Color ?? "#FF0000" });
            LightsChanged(rebuild: true);
        };
        list.Children.Add(add);
        sp.Children.Add(list);
        return sp;
    }

    // ===================== Bausteine =====================
    private Grid Row(string label, UIElement content)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Txt(13, "Muted"); l.Text = label; l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);
        Grid.SetColumn((FrameworkElement)content, 1);
        if (content is FrameworkElement fe) fe.HorizontalAlignment = fe is Slider ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        g.Children.Add(content);
        return g;
    }

    private Border Segmented(string[] labels, int selected, Action<int> onSelect)
    {
        var group = "seg" + Guid.NewGuid().ToString("N");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        int current = selected;   // mitführen: sonst ließe sich ohne Neuaufbau nicht auf den Anfangswert zurückschalten
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var rb = new RadioButton { Content = labels[i], GroupName = group, Style = (Style)FindResource("Seg"), IsChecked = i == selected };
            rb.Checked += (_, _) => { if (idx != current) { current = idx; onSelect(idx); } };
            sp.Children.Add(rb);
        }
        var b = new Border { Child = sp, CornerRadius = new CornerRadius(8), Padding = new Thickness(3) };
        b.SetResourceReference(Border.BackgroundProperty, "Bg");
        return b;
    }

    private StackPanel PercentSlider(int value, Action<int> onChange)
    {
        var sl = new Slider { Minimum = 0, Maximum = 100, Value = value, Width = 300, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        var lbl = Txt(13, "Text", FontWeights.SemiBold); lbl.Width = 50; lbl.Margin = new Thickness(12, 0, 0, 0); lbl.VerticalAlignment = VerticalAlignment.Center;
        lbl.Text = $"{value} %";
        sl.ValueChanged += (_, e) => { lbl.Text = $"{(int)e.NewValue} %"; onChange((int)e.NewValue); };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(sl); sp.Children.Add(lbl);
        return sp;
    }

    /// Farbfeld; Klick öffnet den Farbwähler (Änderungen werden live übernommen).
    private Button Swatch(string hex, Action<string> onChange, bool small = false, Action? returnTo = null)
    {
        var b = new Button { Width = small ? 32 : 44, Height = small ? 32 : 34, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = hex };
        var face = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Rgb.ToMedia(hex)), BorderThickness = new Thickness(1) };
        face.SetResourceReference(Border.BorderBrushProperty, "Line");
        b.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        b.Content = face;
        b.Click += (_, _) => { _colorReturn = returnTo; ShowColorPicker(hex, h =>
        {
            face.Background = new SolidColorBrush(Rgb.ToMedia(h)); b.ToolTip = h; hex = h; onChange(h);
        }); };
        return b;
    }

    /// Wird jede Sekunde aufgerufen: Live-Farbpunkte aktualisieren.
    private void RefreshLightDots()
    {
        foreach (var ((dev, zone), dot) in _zoneDots)
        {
            var c = _app.Lighting.ShownColor(dev, zone);
            dot.Fill = c is { } col ? new SolidColorBrush(Color.FromRgb(col.r, col.g, col.b)) : Brushes.Transparent;
        }
    }

    // ===================== Farbwähler =====================
    private Action<string>? _colorChange;
    private string _colorInitial = "#FFFFFF";
    private bool _colorLoading;

    private static readonly string[] Presets =
    {
        "#FFFFFF", "#FF0000", "#FF4800", "#FFAE00", "#FFFF00", "#BAFF00", "#00FF00", "#0CFF6B",
        "#00FFFF", "#1AC6FF", "#0050FF", "#0000FF", "#8B5CF6", "#AA00FF", "#FF00FF", "#FF1A8C"
    };

    private void ShowColorPicker(string hex, Action<string> onChange)
    {
        _colorInitial = hex; _colorChange = onChange;
        Swatches.Children.Clear();
        foreach (var p in Presets)
        {
            var b = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 8), Background = new SolidColorBrush(Rgb.ToMedia(p)), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            b.MouseLeftButtonUp += (_, _) => SetPickerColor(p, notify: true);
            Swatches.Children.Add(b);
        }
        SetPickerColor(hex, notify: false);
        ShowOverlay(Loc.T("ColorTitle"), ColorPanel);
    }

    private void SetPickerColor(string hex, bool notify)
    {
        var (r, g, b) = Rgb.Parse(hex);
        var (h, s, v) = ToHsv(r, g, b);
        _colorLoading = true;
        HueSlider.Value = h; SatSlider.Value = s * 100; ValSlider.Value = v * 100;
        _colorLoading = false;
        ApplyPicker(Rgb.Hex(r, g, b), notify);
    }

    private void ApplyPicker(string hex, bool notify)
    {
        HexBox.Text = hex;
        ColorPreview.Background = new SolidColorBrush(Rgb.ToMedia(hex));
        if (notify) _colorChange?.Invoke(hex);
    }

    private void Hsv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_colorLoading || HexBox == null) return;
        var (r, g, b) = FromHsv(HueSlider.Value, SatSlider.Value / 100, ValSlider.Value / 100);
        ApplyPicker(Rgb.Hex(r, g, b), notify: true);
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitHex(); }
    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => CommitHex();
    private void CommitHex()
    {
        var t = HexBox.Text.Trim(); if (!t.StartsWith("#")) t = "#" + t;
        if (t.Length == 7 && uint.TryParse(t[1..], System.Globalization.NumberStyles.HexNumber, null, out _)) SetPickerColor(t.ToUpperInvariant(), notify: true);
    }

    private void ColorOk_Click(object sender, RoutedEventArgs e) { _colorChange = null; CloseOverlay(); }

    /// Schließen ohne „Übernehmen“ stellt die ursprüngliche Farbe wieder her.
    private void CancelColorIfOpen()
    {
        if (_colorChange != null && ColorPanel.Visibility == Visibility.Visible) _colorChange(_colorInitial);
        _colorChange = null;
    }

    private static (double h, double s, double v) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
        double h = d == 0 ? 0 : max == rf ? 60 * (((gf - bf) / d) % 6) : max == gf ? 60 * ((bf - rf) / d + 2) : 60 * ((rf - gf) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (byte r, byte g, byte b) FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ===================== Einstellungen: Geräte & Akzentfarbe =====================
    private void FillDeviceSettings()
    {
        FansEnabledBox.IsChecked = Cfg.FansEnabled;
        LightingEnabledBox.IsChecked = Cfg.LightingEnabled;
        LightDeviceList.Children.Clear();
        var names = _app.Lighting.Devices.Select(d => d.Name).Union(Cfg.LightDevices).Distinct().ToList();
        if (names.Count == 0) LightDeviceList.Children.Add(Note(Loc.T("LightSearching")));
        foreach (var n in names)
        {
            var cb = new CheckBox { Content = n, IsChecked = Cfg.LightDevices.Contains(n), FontSize = 13, Margin = new Thickness(0, 4, 0, 0), IsEnabled = Cfg.LightingEnabled };
            cb.Click += (_, _) => ToggleLightDevice(n, cb.IsChecked == true);
            LightDeviceList.Children.Add(cb);
        }
        var exe = LightingEngine.FindServer(Cfg.OpenRgbPath);
        OpenRgbPathText.Text = exe != null ? Loc.T("OpenRgbFound", exe) : Loc.T("OpenRgbMissing");

        AccentFixed.IsChecked = !Cfg.AccentFromLight; AccentLight.IsChecked = Cfg.AccentFromLight;
        AccentColorBtn.Background = new SolidColorBrush(Rgb.ToMedia(Cfg.AccentColor));
        AccentColorBtn.Visibility = Cfg.AccentFromLight ? Visibility.Collapsed : Visibility.Visible;
        var zones = _app.Lighting.Devices.Where(d => Cfg.LightDevices.Contains(d.Name))
            .SelectMany(d => d.Zones.Where(z => z.LedsCount > 0).Select(z => (d.Name, z.Index, Label: $"{d.Name} · {z.Name}"))).ToList();
        AccentZoneBox.ItemsSource = zones.Select(z => z.Label).ToList();
        AccentZoneBox.Tag = zones;
        AccentZoneBox.SelectedIndex = zones.FindIndex(z => z.Name == Cfg.AccentDevice && z.Index == Cfg.AccentZone);
        AccentZoneBox.Visibility = Cfg.AccentFromLight ? Visibility.Visible : Visibility.Collapsed;
        FillUiColorSettings();
    }

    /// Einstellungen für die Farben von Lüfterrädern und Kurve.
    private void FillUiColorSettings()
    {
        UiColorPanel.Children.Clear();
        var zones = _app.Lighting.Devices.Where(d => Cfg.LightDevices.Contains(d.Name))
            .SelectMany(d => d.Zones.Where(z => z.LedsCount > 0).Select(z => (Key: UiColors.ZoneKey(d.Name, z.Index), Label: $"{d.Name} · {z.Name}"))).ToList();
        string[] modes = { Loc.T("UiAccent"), Loc.T("UiFixed"), Loc.T("UiLight"), Loc.T("UiRainbow") };

        void changed() { SaveSoon(); UiColors.Invalidate(); TrackChange(); }

        ComboBox ZoneBox(string current, Action<string> set)
        {
            var cb = new ComboBox { Width = 300, ItemsSource = zones.Select(z => z.Label).ToList(), SelectedIndex = zones.FindIndex(z => z.Key == current) };
            cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(zones[cb.SelectedIndex].Key); changed(); } };
            return cb;
        }

        UiColorPanel.Children.Add(Note(Loc.T("UiRotorHint")));

        // Kurve
        UiColorPanel.Children.Add(Row(Loc.T("UiCurve"), Segmented(modes, (int)Cfg.CurveMode, i => { Cfg.CurveMode = (UiColorMode)i; changed(); FillUiColorSettings(); })));
        if (Cfg.CurveMode == UiColorMode.Fixed)
            UiColorPanel.Children.Add(Row("", Swatch(Cfg.CurveColor, hex => { Cfg.CurveColor = hex; changed(); }, returnTo: () => Settings_Click(this, new RoutedEventArgs()))));
        else if (Cfg.CurveMode == UiColorMode.Light)
            UiColorPanel.Children.Add(Row("", zones.Count > 0 ? ZoneBox(Cfg.CurveZone, k => Cfg.CurveZone = k) : Note(Loc.T("UiNoZones"))));

        // Regenbogen-Tempo
        if (Cfg.CurveMode == UiColorMode.Rainbow)
        {
            var sl = new Slider { Minimum = 5, Maximum = 180, Value = Cfg.RainbowSpeed, Width = 300 };
            sl.ValueChanged += (_, e) => { Cfg.RainbowSpeed = (int)e.NewValue; _rainbowSave.Stop(); _rainbowSave.Start(); };
            UiColorPanel.Children.Add(Row(Loc.T("UiRainbowSpeed"), sl));
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _rainbowSave = CreateSaveTimer();
    /// Verzögert speichern: beim Ziehen im Farbwähler kämen sonst Dutzende Schreibvorgänge pro Sekunde zusammen.
    /// Beim Beenden speichert die App in jedem Fall noch einmal.
    private void SaveSoon() { _rainbowSave.Stop(); _rainbowSave.Start(); }
    private static System.Windows.Threading.DispatcherTimer CreateSaveTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, _) => { t.Stop(); ((App)Application.Current).Config.Save(); };
        return t;
    }

    private void FansEnabled_Click(object sender, RoutedEventArgs e)
    {
        Cfg.FansEnabled = FansEnabledBox.IsChecked == true; Cfg.Save();
        FanController.Log("Lüftersteuerung " + (Cfg.FansEnabled ? "an" : "aus"));
    }

    private void LightingEnabled_Click(object sender, RoutedEventArgs e)
    {
        Cfg.LightingEnabled = LightingEnabledBox.IsChecked == true; Cfg.Save();
        PushLights(); FillDeviceSettings();
        if (TabLight.IsChecked == true) BuildLightView();
    }

    private void ToggleLightDevice(string name, bool on)
    {
        if (on && name.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase) && SystemInfo.AuraServiceRunning())
        {
            // Armoury Crate würde dieselben LEDs steuern: anbieten, dessen Lichtdienst abzuschalten
            if (MessageBox.Show(this, Loc.T("AskAuraService"), Loc.T("Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                if (!SystemInfo.DisableAuraService()) MessageBox.Show(this, Loc.T("AuraServiceFail"), Loc.T("Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (on && !Cfg.LightDevices.Contains(name)) Cfg.LightDevices.Add(name);
        if (!on) Cfg.LightDevices.Remove(name);
        Cfg.Save();
        PushLights(); FillDeviceSettings();
        if (TabLight.IsChecked == true) BuildLightView();
    }

    private void OpenRgbPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "OpenRGB|OpenRGB.exe", Title = "OpenRGB.exe" };
        if (dlg.ShowDialog(this) != true) return;
        Cfg.OpenRgbPath = dlg.FileName; Cfg.Save();
        PushLights(); FillDeviceSettings();
    }

    private void Accent_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Cfg.AccentFromLight = AccentLight.IsChecked == true; Cfg.Save();
        _app.UpdateAccent(force: true); FillDeviceSettings();
    }

    private void AccentColor_Click(object sender, RoutedEventArgs e)
    {
        _colorReturn = () => Settings_Click(this, new RoutedEventArgs());
        ShowColorPicker(Cfg.AccentColor, hex =>
        {
            Cfg.AccentColor = hex; SaveSoon(); _app.UpdateAccent(force: true);
            AccentColorBtn.Background = new SolidColorBrush(Rgb.ToMedia(hex));
        });
    }

    private void AccentZone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccentZoneBox.Tag is not List<(string Name, int Index, string Label)> zones || AccentZoneBox.SelectedIndex < 0) return;
        var z = zones[AccentZoneBox.SelectedIndex];
        Cfg.AccentDevice = z.Name; Cfg.AccentZone = z.Index; Cfg.Save();
        _app.UpdateAccent(force: true);
    }

    /// Leuchteffekt der Kurve an die Akzentfarbe angleichen (Effekte lassen sich nicht per Ressource binden).
    public void AccentChanged() => UiColors.Invalidate();
}
