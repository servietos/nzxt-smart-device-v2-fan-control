using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NzxtFan;

/// Rückgängig, gemeinsame Speicherleiste, ⋯-Menüs und „Lüfter bearbeiten“.
public partial class MainWindow
{
    // ===================== Rückgängig =====================
    // Gemerkt wird alles, was man im Fenster bearbeitet: Kurvenpunkte, Zuordnung, Beleuchtung, Lüfternamen und -farben, Kurvenfarbe.
    private sealed class Snapshot
    {
        public int Sel { get; set; }
        public FanAssign[] Work { get; set; } = Array.Empty<FanAssign>();
        public int[] Draft { get; set; } = Array.Empty<int>();
        public List<LightSetting> Lights { get; set; } = new();
        public string[] FanNames { get; set; } = Array.Empty<string>();
        public UiColorMode[] RotorModes { get; set; } = Array.Empty<UiColorMode>();
        public string[] RotorColors { get; set; } = Array.Empty<string>();
        public string[] RotorZones { get; set; } = Array.Empty<string>();
        public UiColorMode CurveMode { get; set; }
        public string CurveColor { get; set; } = "";
        public string CurveZone { get; set; } = "";
        public int RainbowSpeed { get; set; }
    }

    private readonly Stack<string> _undo = new();
    private string? _current;              // Zustand nach dem letzten Schritt
    private DateTime _lastPush = DateTime.MinValue;
    private bool _restoring;

    private string TakeSnapshot() => JsonSerializer.Serialize(new Snapshot
    {
        Sel = _sel, Work = _work, Draft = _draft, Lights = _workLights, FanNames = Cfg.FanNames,
        RotorModes = Cfg.RotorModes!, RotorColors = Cfg.RotorColors, RotorZones = Cfg.RotorZones,
        CurveMode = Cfg.CurveMode, CurveColor = Cfg.CurveColor, CurveZone = Cfg.CurveZone, RainbowSpeed = Cfg.RainbowSpeed
    });

    /// Nach jeder Änderung aufrufen. Schnelle Folgen (Ziehen, Regler) werden zu einem Schritt zusammengefasst.
    private void TrackChange()
    {
        if (_restoring || _work.Length != 3) return;
        var now = TakeSnapshot();
        if (_current == null) { _current = now; UpdateUndoButton(); return; }
        if (now == _current) return;
        if (DateTime.UtcNow - _lastPush > TimeSpan.FromMilliseconds(700) || _undo.Count == 0) _undo.Push(_current);
        _lastPush = DateTime.UtcNow;
        _current = now;
        if (_undo.Count > 200) { var keep = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        UpdateUndoButton();
    }

    /// Nach Speichern oder Profilwechsel beginnt der Verlauf neu.
    private void ResetUndo() { _undo.Clear(); _current = null; TrackChange(); }

    private void UpdateUndoButton() { UndoBtn.IsEnabled = _undo.Count > 0; UndoBtn.Opacity = _undo.Count > 0 ? 1 : 0.4; }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var s = JsonSerializer.Deserialize<Snapshot>(_undo.Pop())!;
        _restoring = true;
        try
        {
            _sel = Math.Clamp(s.Sel, 0, 2);
            _work = s.Work; _draft = s.Draft; _workLights = s.Lights;
            Cfg.FanNames = s.FanNames; Cfg.RotorModes = s.RotorModes; Cfg.RotorColors = s.RotorColors; Cfg.RotorZones = s.RotorZones;
            Cfg.CurveMode = s.CurveMode; Cfg.CurveColor = s.CurveColor; Cfg.CurveZone = s.CurveZone; Cfg.RainbowSpeed = s.RainbowSpeed;
            Cfg.Save();
            UiColors.Invalidate();
            PushLights();
            if (TabLight.IsChecked == true) BuildLightView();
            if (FanEditPanel.Visibility == Visibility.Visible && Overlay.Visibility == Visibility.Visible) EditFan(_editFan);
            RefreshEditor();
        }
        finally { _restoring = false; }
        _current = TakeSnapshot();
        UpdateUndoButton();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Strg+Z überall außer in Textfeldern (dort gilt das eigene Rückgängig des Feldes)
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox) { Undo(); e.Handled = true; }
    }

    // ===================== Speicherleiste =====================
    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (CurveDirty) SaveCurve();
        if (ProfileDirty) SaveProfile();
        ResetUndo();
        DrawChart(); UpdateDirtyUi(); Refresh();
    }

    private void DiscardAll_Click(object sender, RoutedEventArgs e)
    {
        _work = Cfg.Active.Fans.Select(f => f.Clone()).ToArray();
        _draft = EditCurve.Percent.ToArray();
        _workLights = Cfg.Active.Lights.Select(l => l.Clone()).ToList();
        PushLights();
        if (TabLight.IsChecked == true) BuildLightView();
        ResetUndo();
        RefreshEditor();
    }

    // ===================== ⋯-Menüs =====================
    private void MoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b) { menu.PlacementTarget = b; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
    }

    // ===================== Lüfter bearbeiten (Stift) =====================
    private int _editFan;
    private readonly System.Windows.Threading.DispatcherTimer _nameSave = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// Name, LED-Zone und Farbe des Lüfterrads – alles für einen Lüfter an einer Stelle.
    private void EditFan(int ch)
    {
        _editFan = ch;
        FanEditPanel.Children.Clear();
        var zones = _app.Lighting.Devices.Where(d => Cfg.LightDevices.Contains(d.Name))
            .SelectMany(d => d.Zones.Where(z => z.LedsCount > 0).Select(z => (Key: UiColors.ZoneKey(d.Name, z.Index), Label: $"{d.Name} · {z.Name}"))).ToList();

        // Name
        var name = new TextBox { Text = Cfg.FanNames[ch], Width = 300 };
        name.TextChanged += (_, _) =>
        {
            Cfg.FanNames[ch] = name.Text.Trim();
            OverlayTitle.Text = Loc.T("EditFanTitle", Cfg.FanName(ch));
            EditorTitle.Text = Cfg.FanName(_sel);
            Refresh();
            _nameSave.Stop(); _nameSave.Start();
        };
        FanEditPanel.Children.Add(Row(Loc.T("FanName"), name));
        FanEditPanel.Children.Add(Row("", Note(Loc.T("FanNameHint", ch + 1))));

        // LED-Zone, an der dieser Lüfter hängt
        var zoneBox = new ComboBox { Width = 300 };
        var items = new List<string> { Loc.T("FanZoneNone") }; items.AddRange(zones.Select(z => z.Label));
        zoneBox.ItemsSource = items;
        zoneBox.SelectedIndex = Math.Max(0, zones.FindIndex(z => z.Key == Cfg.RotorZones[ch]) + 1);
        zoneBox.SelectionChanged += (_, _) =>
        {
            Cfg.RotorZones[ch] = zoneBox.SelectedIndex <= 0 ? "" : zones[zoneBox.SelectedIndex - 1].Key;
            Cfg.Save(); UiColors.Invalidate(); TrackChange();
            if (TabLight.IsChecked == true) BuildLightView();
            EditFan(ch);
        };
        FanEditPanel.Children.Add(Row(Loc.T("FanZone"), zones.Count > 0 ? zoneBox : Note(Loc.T("UiNoZones"))));

        // Farbe des Lüfterrads in der Oberfläche
        bool hasZone = !string.IsNullOrEmpty(Cfg.RotorZones[ch]);
        string[] modes = { Loc.T("UiAccent"), Loc.T("UiFixed"), Loc.T("FanLikeLight"), Loc.T("UiRainbow") };
        FanEditPanel.Children.Add(Row(Loc.T("FanWheelColor"), Segmented(modes, (int)Cfg.RotorModes![ch], i =>
        {
            Cfg.RotorModes[ch] = (UiColorMode)i; Cfg.Save(); UiColors.Invalidate(); TrackChange(); EditFan(ch);
        })));
        switch (Cfg.RotorModes[ch])
        {
            case UiColorMode.Fixed:
                FanEditPanel.Children.Add(Row("", Swatch(Cfg.RotorColors[ch], hex => { Cfg.RotorColors[ch] = hex; SaveSoon(); UiColors.Invalidate(); TrackChange(); }, returnTo: () => EditFan(ch))));
                break;
            case UiColorMode.Light when !hasZone:
                FanEditPanel.Children.Add(Row("", Note(Loc.T("FanNeedsZone"))));
                break;
            case UiColorMode.Rainbow:
                var sl = new Slider { Minimum = 5, Maximum = 180, Value = Cfg.RainbowSpeed, Width = 300 };
                sl.ValueChanged += (_, ev) => { Cfg.RainbowSpeed = (int)ev.NewValue; _nameSave.Stop(); _nameSave.Start(); };
                FanEditPanel.Children.Add(Row(Loc.T("UiRainbowSpeed"), sl));
                break;
        }
        ShowOverlay(Loc.T("EditFanTitle", Cfg.FanName(ch)), FanEditPanel);
    }

    /// Verzögertes Speichern für Texteingaben und Regler.
    private void InitEditing()
    {
        _nameSave.Tick += (_, _) => { _nameSave.Stop(); Cfg.Save(); TrackChange(); };
        PreviewKeyDown += Window_PreviewKeyDown;
        UpdateUndoButton();
    }
}
