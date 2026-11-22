using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NzxtFan;

/// Hauptfenster.
/// Zwei getrennte Bearbeitungsstände:
///  - Abstimmung (_work): welche Kurve und Temperaturquelle jeder Lüfter nutzt → „Profil speichern“
///  - Kurvenform (_draft): Punkte der gerade angezeigten Kurve → „Kurve speichern“
/// Die Lüfter folgen immer nur dem gespeicherten Stand.
public partial class MainWindow : Window
{
    private readonly App _app;
    private AppConfig Cfg => _app.Config;

    private int _sel;                                   // ausgewählter Lüfterkanal 0–2
    private FanAssign[] _work = Array.Empty<FanAssign>();
    private int[] _draft = Curve.DefaultPercent.ToArray();
    private bool _loading;                              // verhindert Rückkopplung beim Setzen von Steuerelementen

    private bool ProfileDirty => _work.Where((w, i) => !w.SameAs(Cfg.Active.Fans[i])).Any() || LightsDirty;
    private CurveDef EditCurve => Cfg.CurveById(_work[_sel].CurveId) ?? Cfg.Curves[0];
    private bool CurveDirty => !_draft.SequenceEqual(EditCurve.Percent);

    // Kacheln (Index = Kanal)
    private readonly TextBlock[] _tName = new TextBlock[3], _tRpm = new TextBlock[3], _tDuty = new TextBlock[3], _tTemp = new TextBlock[3], _tSrc = new TextBlock[3], _tCurve = new TextBlock[3];
    private readonly Border[] _tCard = new Border[3], _tBar = new Border[3];
    private readonly RotateTransform?[] _rotor = new RotateTransform?[3];
    private readonly double[] _angle = new double[3];
    private TimeSpan _lastFrame, _lastColorUpdate;
    private DateTime _lastCamCheck = DateTime.MinValue;   // Prozessliste nicht öfter als alle 5 s durchsuchen
    private static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush BadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private readonly bool[] _tStalled = new bool[3];
    private readonly DateTime?[] _emptySince = new DateTime?[3];   // seit wann der Controller an diesem Kanal keinen Lüfter meldet
    // Inhalt des Info-Symbols ⓘ im Kurven-Editor: Verwendung der Kurve und Bedienhinweis
    private readonly TextBlock UsageText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _hintTip = new() { TextWrapping = TextWrapping.Wrap };
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private Point _dragOrigin; private int _dragTile = -1; private bool _tileDragged;

    // Diagramm
    private const double PadL = 44, PadB = 30, PadT = 10, PadR = 14;
    private readonly Ellipse[] _pts = new Ellipse[17];
    private DropShadowEffect? _glow;
    private Polyline? _line, _savedLine; private Polygon? _area; private Line? _nowLine; private Ellipse? _nowDot; private TextBlock? _nowLbl; private Border? _tip;
    private int _drag = -1; private int[]? _dragStart;

    // Namenseingabe im Overlay
    private Func<string, string?>? _nameCommit;   // liefert Fehlermeldung oder null
    private bool _nameAllowEmpty;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && Cfg.MinimizeToTrayOnly) Hide(); };
        Closing += (_, e) => { if (!_app.Exiting) { e.Cancel = true; Hide(); } };
        UiColors.Invalidate(); UiColors.Update(Cfg, _app.Lighting, 0);   // Pinsel existieren, bevor gezeichnet wird
        // Animation nur bei sichtbarem Fenster: im Infobereich soll die App nicht 60-mal pro Sekunde aufwachen
        IsVisibleChanged += (_, _) => { CompositionTarget.Rendering -= SpinRotors; if (IsVisible) CompositionTarget.Rendering += SpinRotors; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible) { CloseOverlay(); e.Handled = true; } };
        Chart.LostMouseCapture += (_, _) => { _drag = -1; _dragStart = null; if (_tip != null) _tip.Visibility = Visibility.Collapsed; UpdateDirtyUi(); };
        ResetWork();
        ApplyLanguage();
        RebuildTiles();
        SelectFan(Cfg.FanOrder[0], ask: false);
        InitEditing();
    }

    // ===================== Sprache / Theme =====================
    private void ApplyLanguage()
    {
        Title = TitleText.Text = Loc.T("Title");
        CamText.Text = Loc.T("CamWarning");
        ProfileLbl.Text = Loc.T("Profile");
        MenuDuplicate.Header = Loc.T("Duplicate"); MenuRenameProfile.Header = Loc.T("ProfileRename"); MenuDeleteProfile.Header = Loc.T("ProfileDelete");
        ProfileMoreBtn.ToolTip = Loc.T("MoreProfile"); CurveMoreBtn.ToolTip = Loc.T("MoreCurve"); UndoBtn.ToolTip = Loc.T("Undo");
        SaveBarSave.Content = Loc.T("Save"); SaveBarDiscard.Content = Loc.T("Discard");
        CurveLbl.Text = Loc.T("Curve"); TempFromLbl.Text = Loc.T("TempFrom"); ApplyAllBtn.Content = Loc.T("ApplyAll");
        MenuCurveSaveAs.Header = Loc.T("CurveSaveAs"); MenuCurveRename.Header = Loc.T("CurveRename"); MenuCurveDelete.Header = Loc.T("CurveDelete");
        _hintTip.Text = Loc.T("Hint");
        if (HintText.ToolTip == null)
        {
            var tip = new StackPanel { MaxWidth = 400 };
            tip.Children.Add(UsageText); tip.Children.Add(_hintTip);
            HintText.ToolTip = new ToolTip { Content = tip };
        }
        SettingsBtn.ToolTip = Loc.T("Settings"); HelpBtn.ToolTip = Loc.T("Help");
        OverlayCloseBtn.Content = Loc.T("Close"); NameHintText.Text = Loc.T("NameHint");
        SetAppearanceLbl.Text = Loc.T("SetAppearance"); ThemeDark.Content = Loc.T("SetDark"); ThemeLight.Content = Loc.T("SetLight");
        SetLanguageLbl.Text = Loc.T("SetLanguage"); SetWindowLbl.Text = Loc.T("SetWindow");
        MinTray.Content = Loc.T("SetMinTray"); MinBoth.Content = Loc.T("SetMinBoth"); SetCloseNote.Text = Loc.T("SetCloseNote");
        SetStartLbl.Text = Loc.T("SetStart"); AutostartBox.Content = Loc.T("SetAutostart");
        SetSystemLbl.Text = Loc.T("SetSystem"); OpenFolderBtn.Content = Loc.T("OpenFolder");
        TabFans.Content = Loc.T("TabFans"); TabLight.Content = Loc.T("TabLight");
        SetDevicesLbl.Text = Loc.T("SetDevices"); FansEnabledBox.Content = Loc.T("SetFansEnabled"); LightingEnabledBox.Content = Loc.T("SetLightingEnabled");
        OpenRgbPathBtn.Content = Loc.T("OpenRgbChoose");
        SetAccentLbl.Text = Loc.T("SetAccent"); AccentFixed.Content = Loc.T("AccentFixed"); AccentLight.Content = Loc.T("AccentLight");
        HueLbl.Text = Loc.T("ColorHue"); SatLbl.Text = Loc.T("ColorSat"); ValLbl.Text = Loc.T("ColorVal"); ColorOkBtn.Content = Loc.T("ColorOk");
        if (TabLight.IsChecked == true) BuildLightView();
        _app.UpdateTrayTexts();
        LoadProfileList(); LoadCurveList();
    }

    private void ApplyTheme()
    {
        Theme.Apply(Cfg.DarkTheme);
        Theme.ApplyTitleBar(this);
        RebuildTiles();
        RefreshEditor();
    }

    // ===================== Arbeitsstand =====================
    private void ResetWork()
    {
        _work = Cfg.Active.Fans.Select(f => f.Clone()).ToArray();
        _draft = EditCurve.Percent.ToArray();
        _workLights = Cfg.Active.Lights.Select(l => l.Clone()).ToList();
        PushLights();
        if (TabLight.IsChecked == true) BuildLightView();
    }

    private void UpdateDirtyUi()
    {
        bool p = ProfileDirty, c = CurveDirty;
        // Eine Leiste für alles: sagt, was ungespeichert ist
        var parts = new List<string>();
        if (c) parts.Add(Loc.T("DirtyCurve", EditCurve.Name));
        if (p) parts.Add(Loc.T("DirtyProfile", Cfg.ActiveProfile));
        // Während ein Kurvenpunkt gezogen wird, die Leiste nicht ein- oder ausblenden: das ändert die Höhe der Kurve,
        // der Punkt springt unter der Maus weg, der Wert kippt zurück und die Leiste flackert. Nachgeholt wird beim Loslassen.
        if (_drag < 0) SaveBar.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveBarText.Text = Loc.T("DirtyPrefix") + " " + string.Join(" · ", parts);
        if (_savedLine != null) _savedLine.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
        UsageText.Text = UsageDescription(EditCurve.Id);
        MenuDeleteProfile.IsEnabled = Cfg.Profiles.Count > 1;
        MenuCurveDelete.IsEnabled = Cfg.Curves.Count > 1;
        TrackChange();
    }

    /// Fragt bei ungespeicherter Kurve nach. false = Abbrechen.
    private bool ConfirmCurve()
    {
        if (!CurveDirty) return true;
        var r = Ask(Loc.T("AskCurve", EditCurve.Name));
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) SaveCurve(); else _draft = EditCurve.Percent.ToArray();
        return true;
    }

    /// Fragt bei ungespeicherter Abstimmung nach. false = Abbrechen.
    private bool ConfirmProfile()
    {
        if (!ConfirmCurve()) return false;
        if (!ProfileDirty) return true;
        var r = Ask(Loc.T("AskProfile", Cfg.ActiveProfile));
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) SaveProfile(); else ResetWork();
        return true;
    }

    private MessageBoxResult Ask(string text) =>
        MessageBox.Show(this, text, Loc.T("Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

    private void SaveProfile()
    {
        for (int i = 0; i < 3; i++) Cfg.Active.Fans[i] = _work[i].Clone();
        Cfg.Active.Lights = _workLights.Select(l => l.Clone()).ToList();
        Cfg.Save();
        FanController.Log($"Profil '{Cfg.ActiveProfile}' gespeichert.");
    }

    private void SaveCurve()
    {
        EditCurve.Percent = _draft.ToArray();
        Cfg.Save();
        FanController.Log($"Kurve '{EditCurve.Name}' gespeichert.");
    }

    // ===================== Kacheln =====================
    private static TextBlock Txt(double size, string fg = "Text", FontWeight? w = null)
    {
        var t = new TextBlock { FontSize = size, FontWeight = w ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        return t;
    }

    private void RebuildTiles()
    {
        CpuCard.Effect = Theme.Shadow(4, 18); GpuCard.Effect = Theme.Shadow(4, 18); CurveCard.Effect = Theme.Shadow(); OverlayCard.Effect = Theme.Shadow(8, 40);
        Tiles.Children.Clear();
        foreach (var ch in Cfg.FanOrder) Tiles.Children.Add(BuildTile(ch));
        HighlightSelected();
    }

    private Border BuildTile(int ch)
    {
        // Name + Stift zum Umbenennen
        _tName[ch] = Txt(15, "Text", FontWeights.SemiBold);
        var pen = new Button { Content = "✎", Style = (Style)FindResource("Btn"), Padding = new Thickness(6, 0, 6, 0), Height = 24, FontSize = 12, ToolTip = Loc.T("EditFan"), Margin = new Thickness(8, 0, 0, 0) };
        pen.Click += (_, e) => { e.Handled = true; EditFan(ch); };
        var nameRow = new DockPanel(); DockPanel.SetDock(pen, Dock.Right); nameRow.Children.Add(pen); nameRow.Children.Add(_tName[ch]);

        _tRpm[ch] = Txt(38, "Text", FontWeights.Bold); _tRpm[ch].Margin = new Thickness(0, 6, 0, 0);
        var unit = Txt(12, "Muted"); unit.Text = Loc.T("Rpm");
        _tDuty[ch] = Txt(15); _tDuty[ch].Margin = new Thickness(0, 10, 0, 6);
        var barBg = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        barBg.SetResourceReference(Border.BackgroundProperty, "Line");
        _tBar[ch] = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _tBar[ch].SetResourceReference(Border.BackgroundProperty, $"RotorBar{ch}");
        var bar = new Grid(); bar.Children.Add(barBg); bar.Children.Add(_tBar[ch]);
        var tempRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _tTemp[ch] = Txt(24, "Text", FontWeights.SemiBold);
        _tSrc[ch] = Txt(13, "Muted"); _tSrc[ch].VerticalAlignment = VerticalAlignment.Bottom; _tSrc[ch].Margin = new Thickness(8, 0, 0, 4);
        tempRow.Children.Add(_tTemp[ch]); tempRow.Children.Add(_tSrc[ch]);
        _tCurve[ch] = Txt(12, "Muted"); _tCurve[ch].Margin = new Thickness(0, 6, 0, 0);

        var left = new StackPanel();
        foreach (var el in new UIElement[] { _tRpm[ch], unit, _tDuty[ch], bar, tempRow, _tCurve[ch] }) left.Children.Add(el);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(left);
        var rotor = BuildRotor(ch, 100);
        Grid.SetColumn(rotor, 1); rotor.Margin = new Thickness(12, 4, 0, 0); rotor.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(rotor);
        var root = new StackPanel(); root.Children.Add(nameRow); root.Children.Add(body);

        var lift = new TranslateTransform();
        var card = new Border
        {
            Child = root, CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 14, 18, 16),
            Margin = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(2), Cursor = Cursors.Hand,
            Effect = Theme.Shadow(), RenderTransform = lift, BorderBrush = Brushes.Transparent, AllowDrop = true, ToolTip = Loc.T("TileHint")
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardGradient");
        card.MouseEnter += (_, _) => { lift.Y = -3; card.Effect = Theme.Shadow(10, 28); };
        card.MouseLeave += (_, _) => { lift.Y = 0; card.Effect = Theme.Shadow(); };
        // Klick = auswählen, Ziehen = verschieben
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;   // Stift-Knopf nicht als Ziehen werten
            _dragOrigin = e.GetPosition(this); _dragTile = ch; _tileDragged = false;
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (_dragTile != ch || e.LeftButton != MouseButtonState.Pressed) return;
            if ((e.GetPosition(this) - _dragOrigin).Length < 10) return;
            _tileDragged = true; card.Opacity = 0.6;
            DragDrop.DoDragDrop(card, ch, DragDropEffects.Move);
            card.Opacity = 1; _dragTile = -1;
        };
        card.MouseLeftButtonUp += (_, _) => { if (!_tileDragged && _dragTile == ch) SelectFan(ch); _dragTile = -1; };
        card.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(int)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        card.Drop += (_, e) => { if (e.Data.GetData(typeof(int)) is int from && from != ch) MoveTile(from, ch); };
        _tCard[ch] = card;
        return card;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur != null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    private void MoveTile(int fromCh, int toCh)
    {
        var order = Cfg.FanOrder.ToList();
        int target = order.IndexOf(toCh);
        order.Remove(fromCh);
        order.Insert(target, fromCh);
        Cfg.FanOrder = order.ToArray();
        Cfg.Save();
        RebuildTiles();
        Refresh();
    }

    private void HighlightSelected()
    {
        for (int k = 0; k < 3; k++)
        {
            if (_tCard[k] == null) continue;
            if (k == _sel) _tCard[k].SetResourceReference(Border.BorderBrushProperty, "Accent");
            else _tCard[k].BorderBrush = Brushes.Transparent;
        }
    }


    /// Lüfterrad mit Rahmen, schrägen Blättern und glänzender Nabe; dreht sich passend zur Drehzahl.
    private Grid BuildRotor(int ch, double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse
        {
            StrokeThickness = 5,
            Stroke = new LinearGradientBrush(Color.FromArgb(200, 255, 255, 255), Color.FromArgb(60, 0, 0, 0), 45) { Opacity = Theme.Dark ? 0.35 : 0.8 },
            Fill = new RadialGradientBrush(Theme.C("Hub"), Theme.Dark ? Color.FromRgb(0x12, 0x14, 0x1A) : Color.FromRgb(0xC9, 0xCE, 0xDA)) { GradientOrigin = new Point(0.35, 0.3) },
            Effect = new DropShadowEffect { ShadowDepth = 3, BlurRadius = 10, Opacity = 0.4, Direction = 270 }
        });
        var canvas = new Canvas { Width = size, Height = size };
        _rotor[ch] = new RotateTransform(_angle[ch], size / 2, size / 2);
        canvas.RenderTransform = _rotor[ch];
        double u = size / 100.0;
        var bladeGeo = Geometry.Parse(FormattableString.Invariant(
            $"M0,{-9 * u} C{14 * u},{-14 * u} {22 * u},{-26 * u} {14 * u},{-36 * u} C{6 * u},{-28 * u} {-2 * u},{-19 * u} {-7 * u},{-8 * u} Z"));
        for (int k = 0; k < 7; k++)
        {
            var p = new Path { Data = bladeGeo, Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), StrokeThickness = 0.8 };
            p.SetResourceReference(Shape.FillProperty, $"RotorBlade{ch}");   // Farbe je Lüfter, live (siehe UiColors)
            p.RenderTransform = new TransformGroup { Children = { new RotateTransform(360.0 / 7 * k), new TranslateTransform(size / 2, size / 2) } };
            canvas.Children.Add(p);
        }
        canvas.Effect = new DropShadowEffect { ShadowDepth = 2, BlurRadius = 6, Opacity = 0.45, Direction = 300 };
        g.Children.Add(canvas);
        var hub = new Ellipse { Width = size * 0.26, Height = size * 0.26, Effect = new DropShadowEffect { ShadowDepth = 2, BlurRadius = 6, Opacity = 0.5, Direction = 300 } };
        hub.SetResourceReference(Shape.FillProperty, $"RotorHub{ch}");
        g.Children.Add(hub);
        return g;
    }

    private void SpinRotors(object? sender, EventArgs e)
    {
        if (!IsVisible || e is not RenderingEventArgs re) return;
        // Pinsel/Ressourcen nur ca. 20x pro Sekunde neu bauen (Regenbogen erzeugt sonst jedes Bild ~11 Pinsel); die Drehung bleibt pro Bild flüssig
        if ((re.RenderingTime - _lastColorUpdate).TotalSeconds >= 0.05 || re.RenderingTime < _lastColorUpdate)
        {
            _lastColorUpdate = re.RenderingTime;
            UiColors.Update(Cfg, _app.Lighting, re.RenderingTime.TotalSeconds);
            if (_glow != null && _glow.Color != UiColors.CurveGlow) _glow.Color = UiColors.CurveGlow;
        }
        double dt = _lastFrame == TimeSpan.Zero ? 0 : (re.RenderingTime - _lastFrame).TotalSeconds;
        _lastFrame = re.RenderingTime;
        if (dt <= 0 || dt > 0.5) return;
        var fans = _app.Controller.Fans;
        for (int i = 0; i < 3; i++)
        {
            if (_rotor[i] is not { } r) continue;
            _angle[i] = (_angle[i] + fans[i].Rpm / 60.0 * 360 * dt * 0.12) % 360;   // sichtbar verlangsamt
            r.Angle = _angle[i];
        }
    }

    /// Wird jede Sekunde vom App-Timer aufgerufen.
    public void Refresh()
    {
        var c = _app.Controller; var s = c.Sensors;
        CpuTemp.Text = s.Cpu is double cp ? $"{cp:0} °C" : "–";
        GpuTemp.Text = s.Gpu is double gp ? $"{gp:0} °C" : "–";
        StatusText.Text = c.Status;
        StatusDot.Fill = c.Connected ? OkBrush : BadBrush;
        if (CamBanner.Tag == null || (c.Ticks % 5 == 1 && (DateTime.UtcNow - _lastCamCheck).TotalSeconds >= 5))
        {
            CamBanner.Tag = true; _lastCamCheck = DateTime.UtcNow; CamBanner.Visibility = SystemInfo.CamRunning() ? Visibility.Visible : Visibility.Collapsed;
        }
        bool pawn = _app.PawnIo;
        PawnText.Text = pawn ? Loc.T("CpuUnavailable") : Loc.T("PawnMissing");
        PawnBanner.Visibility = !pawn || (s.Cpu == null && c.Ticks > 3) ? Visibility.Visible : Visibility.Collapsed;

        var fans = c.Fans; var target = c.TargetPercent; var sent = c.SentPercent;
        for (int ch = 0; ch < 3; ch++)
        {
            if (_tName[ch] == null) continue;
            _tName[ch].Text = Cfg.FanName(ch);
            _tRpm[ch].Text = fans[ch].Rpm.ToString("N0");
            bool stalled = c.Stalled[ch];
            _tDuty[ch].Text = $"{fans[ch].Duty} %" + (fans[ch].Duty != target[ch] ? $"  →  {target[ch]} %" : "")
                + (sent[ch] != target[ch] && sent[ch] > 0 ? $"  ({Loc.T("SentPct")} {sent[ch]} %)" : "")   // z. B. Startschub über dem Kurvenwert
                + (stalled ? $"  ⚠ {Loc.T("FanStalledShort")}" : "");
            // Freier Kanal: der Controller meldet dort keinen Lüfter (Modus 0). Erst nach 5 s werten,
            // weil direkt nach dem Verbinden noch die Lüftererkennung läuft. Kachel abblenden und beschriften.
            _emptySince[ch] = c.Connected && fans[ch].Mode == 0 ? _emptySince[ch] ?? DateTime.UtcNow : null;
            bool unplugged = _emptySince[ch] is DateTime since && DateTime.UtcNow - since > TimeSpan.FromSeconds(5);
            if (unplugged) _tDuty[ch].Text = Loc.T("FanNotConnected");
            double op = unplugged ? 0.45 : 1;
            if (_tCard[ch] != null && _tCard[ch].Opacity != op) _tCard[ch].Opacity = op;
            if (stalled != _tStalled[ch])
            {
                _tStalled[ch] = stalled;   // Farbe nur beim Wechsel setzen
                _tDuty[ch].SetResourceReference(TextBlock.ForegroundProperty, stalled ? "Warn" : "Text");
                _tRpm[ch].SetResourceReference(TextBlock.ForegroundProperty, stalled ? "Warn" : "Text");
            }
            var w = ((FrameworkElement)_tBar[ch].Parent).ActualWidth;
            _tBar[ch].Width = Math.Max(0, w * fans[ch].Duty / 100.0);
            // Kachel zeigt den wirksamen (gespeicherten) Stand
            var fa = Cfg.Active.Fans[ch];
            _tTemp[ch].Text = s.Get(fa.Source) is double tv ? $"{tv:0} °C" : "–";
            _tSrc[ch].Text = fa.Source.ToString();
            var curveName = Cfg.CurveById(fa.CurveId)?.Name ?? "?";
            _tCurve[ch].Text = $"{Loc.T("Curve")}: {curveName}" + (_work.Length == 3 && !_work[ch].SameAs(fa) ? $"  ({Loc.T("NotSaved")})" : "");
        }
        UpdateNowMarker();
        if (LightPanel.Visibility == Visibility.Visible)
        {
            // Geräte erscheinen erst nach der Erkennung durch OpenRGB: Ansicht dann einmal neu aufbauen
            int n = _app.Lighting.Devices.Count;
            if (n != _lastLightDeviceCount) { _lastLightDeviceCount = n; BuildLightView(); }
            RefreshLightDots();
        }
    }
    private int _lastLightDeviceCount = -1;

    // ===================== Auswahl =====================
    /// ask: bei Kanalwechsel nach ungespeicherter Kurve fragen. Der Kurvenentwurf bleibt erhalten, solange derselbe Lüfter gewählt bleibt.
    private void SelectFan(int ch, bool ask = true)
    {
        if (ch != _sel && ask && !ConfirmCurve()) return;
        bool reload = ch != _sel || !CurveDirty;
        _sel = ch;
        if (reload) _draft = EditCurve.Percent.ToArray();
        HighlightSelected();
        EditorTitle.Text = Cfg.FanName(ch);
        SyncAssignControls();
        DrawChart();
        UpdateDirtyUi();
        Refresh();
    }

    private void SyncAssignControls()
    {
        _loading = true;
        SrcCpu.IsChecked = _work[_sel].Source == TempSource.CPU; SrcGpu.IsChecked = _work[_sel].Source == TempSource.GPU;
        CurveBox.SelectedIndex = Cfg.Curves.FindIndex(c => c.Id == _work[_sel].CurveId);
        _loading = false;
    }

    // ===================== Profile =====================
    private void LoadProfileList()
    {
        _loading = true;
        ProfileBox.ItemsSource = Cfg.Profiles.Select(p => p.Name).ToList();
        ProfileBox.SelectedItem = Cfg.ActiveProfile;
        _loading = false;
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string name || name == Cfg.ActiveProfile) return;
        if (!ConfirmProfile()) { LoadProfileList(); return; }
        Cfg.ActiveProfile = name; Cfg.Save();
        FanController.Log($"Profil '{name}' aktiviert.");
        ResetWork(); ResetUndo(); RefreshEditor();
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e) { SaveProfile(); UpdateDirtyUi(); Refresh(); }

    private void ProfileDiscard_Click(object sender, RoutedEventArgs e)
    {
        // Nur nachfragen, wenn sich dadurch die Kurve im Editor ändert
        bool curveChanges = Cfg.Active.Fans[_sel].CurveId != _work[_sel].CurveId;
        if (curveChanges && !ConfirmCurve()) return;
        _work = Cfg.Active.Fans.Select(f => f.Clone()).ToArray();
        if (curveChanges) _draft = EditCurve.Percent.ToArray();
        _workLights = Cfg.Active.Lights.Select(l => l.Clone()).ToList();
        PushLights();
        if (TabLight.IsChecked == true) BuildLightView();
        RefreshEditor();
    }

    /// Editor und Kacheln nach Änderungen am Arbeitsstand neu darstellen (ohne den Kurvenentwurf zu verwerfen).
    private void RefreshEditor()
    {
        HighlightSelected();
        EditorTitle.Text = Cfg.FanName(_sel);
        SyncAssignControls(); DrawChart(); UpdateDirtyUi(); Refresh();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var baseName = Cfg.ActiveProfile + Loc.T("CopySuffix");
        AskName(Loc.T("Duplicate"), UniqueProfileName(baseName), name =>
        {
            if (Cfg.Profiles.Any(p => p.Name == name)) return Loc.T("NameExists");
            // Kopie übernimmt den aktuellen Arbeitsstand, das Original bleibt unverändert; der Kurvenentwurf bleibt erhalten
            Cfg.Profiles.Add(new Profile { Name = name, Fans = _work.Select(f => f.Clone()).ToArray(), Lights = _workLights.Select(l => l.Clone()).ToList() });
            Cfg.ActiveProfile = name; Cfg.Save();
            LoadProfileList(); RefreshEditor();
            return null;
        });
    }

    private string UniqueProfileName(string n) { var r = n; int k = 2; while (Cfg.Profiles.Any(p => p.Name == r)) r = $"{n} {k++}"; return r; }

    private void RenameProfile_Click(object sender, RoutedEventArgs e) => AskName(Loc.T("ProfileRename"), Cfg.ActiveProfile, name =>
    {
        if (name == Cfg.ActiveProfile) return null;
        if (Cfg.Profiles.Any(p => p.Name == name)) return Loc.T("NameExists");
        Cfg.Active.Name = name; Cfg.ActiveProfile = name; Cfg.Save(); LoadProfileList(); UpdateDirtyUi();
        return null;
    });

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Cfg.Profiles.Count <= 1) return;
        if (MessageBox.Show(this, Loc.T("AskDeleteProfile", Cfg.ActiveProfile), Loc.T("Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!ConfirmCurve()) return;
        Cfg.Profiles.Remove(Cfg.Active);
        Cfg.ActiveProfile = Cfg.Profiles[0].Name; Cfg.Save();
        FanController.Log($"Profil gelöscht, aktiv: '{Cfg.ActiveProfile}'.");
        ResetWork(); LoadProfileList(); RefreshEditor();
        ResetUndo();   // „Rückgängig“ darf nicht den Stand des gelöschten Profils ins neue aktive Profil laden
    }

    // ===================== Zuordnung (Teil des Profils) =====================
    private void LoadCurveList()
    {
        _loading = true;
        CurveBox.ItemsSource = Cfg.Curves.Select(c => c.Name).ToList();
        if (_work.Length == 3) CurveBox.SelectedIndex = Cfg.Curves.FindIndex(c => c.Id == _work[_sel].CurveId);
        _loading = false;
    }

    private void CurveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CurveBox.SelectedIndex < 0) return;
        var c = Cfg.Curves[CurveBox.SelectedIndex];
        if (c.Id == _work[_sel].CurveId) return;
        if (!ConfirmCurve()) { SyncAssignControls(); return; }
        _work[_sel].CurveId = c.Id;
        _draft = c.Percent.ToArray();
        DrawChart(); UpdateDirtyUi(); Refresh();
    }

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _work[_sel].Source = SrcGpu.IsChecked == true ? TempSource.GPU : TempSource.CPU;
        UpdateDirtyUi(); UpdateNowMarker(); Refresh();
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < 3; i++) _work[i] = _work[_sel].Clone();
        UpdateDirtyUi(); Refresh();
    }

    // ===================== Kurvenbibliothek =====================
    private string UsageDescription(Guid id)
    {
        var uses = Cfg.Profiles.SelectMany(p => p.Fans.Select((f, i) => (p, f, i)))
            .Where(x => x.f.CurveId == id).Select(x => $"{x.p.Name} ({Cfg.FanName(x.i)})").ToList();
        var name = Cfg.CurveById(id)?.Name ?? "";
        return uses.Count == 0 ? Loc.T("CurveUnused", name) : Loc.T("CurveUsedBy", name, string.Join(", ", uses));
    }

    private void CurveSave_Click(object sender, RoutedEventArgs e) { SaveCurve(); DrawChart(); UpdateDirtyUi(); }

    private void CurveDiscard_Click(object sender, RoutedEventArgs e) { _draft = EditCurve.Percent.ToArray(); DrawChart(); UpdateDirtyUi(); }

    private void CurveSaveAs_Click(object sender, RoutedEventArgs e) => AskName(Loc.T("CurveSaveAs"), UniqueCurveName(EditCurve.Name), name =>
    {
        if (Cfg.Curves.Any(c => c.Name == name)) return Loc.T("NameExists");
        var nc = new CurveDef { Name = name, Percent = _draft.ToArray() };
        Cfg.Curves.Add(nc); Cfg.Save();
        // Die alte Kurve bleibt unverändert; der Lüfter nutzt im Arbeitsstand die neue
        _work[_sel].CurveId = nc.Id;
        LoadCurveList(); DrawChart(); UpdateDirtyUi(); Refresh();
        return null;
    });

    private string UniqueCurveName(string n) { var r = n; int k = 2; while (Cfg.Curves.Any(c => c.Name == r)) r = $"{n} ({k++})"; return r; }

    private void CurveRename_Click(object sender, RoutedEventArgs e) => AskName(Loc.T("CurveRename"), EditCurve.Name, name =>
    {
        if (name == EditCurve.Name) return null;
        if (Cfg.Curves.Any(c => c.Name == name)) return Loc.T("NameExists");
        EditCurve.Name = name; Cfg.Save(); LoadCurveList(); UpdateDirtyUi(); Refresh();
        return null;
    });

    private void CurveDelete_Click(object sender, RoutedEventArgs e)
    {
        var cur = EditCurve;
        if (Cfg.Curves.Count <= 1) return;
        // Verwendungen außer dem gerade bearbeiteten Lüfter im aktiven Profil
        var others = Cfg.Profiles.SelectMany(p => p.Fans.Select((f, i) => (p, f, i)))
            .Where(x => x.f.CurveId == cur.Id && !(x.p == Cfg.Active && x.i == _sel))
            .Select(x => $"{x.p.Name} ({Cfg.FanName(x.i)})").ToList();
        others.AddRange(_work.Select((f, i) => (f, i)).Where(x => x.i != _sel && x.f.CurveId == cur.Id && Cfg.Active.Fans[x.i].CurveId != cur.Id)
            .Select(x => $"{Cfg.ActiveProfile} ({Cfg.FanName(x.i)}, {Loc.T("NotSaved")})"));
        if (others.Count > 0)
        {
            MessageBox.Show(this, Loc.T("CurveInUse", cur.Name, string.Join(", ", others.Distinct())), Loc.T("Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var fallback = Cfg.Curves.First(c => c.Id != cur.Id);
        bool savedUse = Cfg.Active.Fans[_sel].CurveId == cur.Id;
        var extra = savedUse ? Loc.T("DeleteCurveFallback", Cfg.FanName(_sel), fallback.Name) : "";
        if (MessageBox.Show(this, Loc.T("AskDeleteCurve", cur.Name, extra), Loc.T("Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Cfg.Curves.Remove(cur);
        _work[_sel].CurveId = fallback.Id;
        if (Cfg.Active.Fans[_sel].CurveId == cur.Id) Cfg.Active.Fans[_sel].CurveId = fallback.Id;   // gespeicherter Stand darf nie auf eine gelöschte Kurve zeigen
        Cfg.Save();
        _draft = fallback.Percent.ToArray();
        FanController.Log($"Kurve '{cur.Name}' gelöscht.");
        LoadCurveList(); RefreshEditor();
        ResetUndo();   // „Rückgängig“ darf die gelöschte Kurve nicht wieder einem Lüfter zuweisen
    }

    // ===================== Overlay: Name, Einstellungen, Hilfe =====================
    private void ShowOverlay(string title, UIElement panel)
    {
        if (panel != ColorPanel) CancelColorIfOpen();
        NamePanel.Visibility = SettingsPanel.Visibility = HelpPanel.Visibility = ColorPanel.Visibility = FanEditPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        OverlayTitle.Text = title;
        Overlay.Visibility = Visibility.Visible;
    }

    private void CloseOverlay()
    {
        CancelColorIfOpen();
        // Farbwähler aus den Einstellungen geöffnet: zurück zu den Einstellungen statt ganz zu schließen
        if (ColorPanel.Visibility == Visibility.Visible && _colorReturn != null) { var back = _colorReturn; _colorReturn = null; back(); return; }
        Overlay.Visibility = Visibility.Collapsed; _nameCommit = null;
    }
    private Action? _colorReturn;   // wohin der Farbwähler beim Schließen zurückkehrt (Einstellungen, Lüfter bearbeiten)
    private void OverlayClose_Click(object sender, RoutedEventArgs e) => CloseOverlay();
    private void Overlay_BackdropClick(object sender, MouseButtonEventArgs e) => CloseOverlay();

    private void AskName(string title, string initial, Func<string, string?> commit, bool allowEmpty = false)
    {
        _nameCommit = commit; _nameAllowEmpty = allowEmpty;
        NameBox.Text = initial;
        NameHintText.Text = Loc.T(allowEmpty ? "NameHintFan" : "NameHint");
        NameHintText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        ShowOverlay(title, NamePanel);
        Dispatcher.BeginInvoke(() => { NameBox.Focus(); NameBox.SelectAll(); });
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseOverlay(); return; }
        if (e.Key != Key.Enter) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0 && !_nameAllowEmpty) return;
        var error = _nameCommit?.Invoke(name);
        if (error != null) { NameHintText.Text = error; NameHintText.SetResourceReference(TextBlock.ForegroundProperty, "Warn"); return; }   // Fenster bleibt offen
        CloseOverlay();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ThemeDark.IsChecked = Cfg.DarkTheme; ThemeLight.IsChecked = !Cfg.DarkTheme;
        LangDe.IsChecked = !Loc.En; LangEn.IsChecked = Loc.En;
        MinTray.IsChecked = Cfg.MinimizeToTrayOnly; MinBoth.IsChecked = !Cfg.MinimizeToTrayOnly;
        FillDeviceSettings();
        AutostartBox.IsEnabled = false;
        _ = Task.Run(SystemInfo.AutostartEnabled).ContinueWith(t => Dispatcher.Invoke(() => { AutostartBox.IsChecked = t.Result; AutostartBox.IsEnabled = true; }));
        PawnStatus.Text = (_app.PawnIo ? "✔ " : "✖ ") + (_app.PawnIo ? Loc.T("PawnOk") : Loc.T("PawnNo"));
        PawnStatus.Foreground = _app.PawnIo ? OkBrush : Theme.B("Warn");
        AppVersionText.Text = Loc.T("AppVersion") + " " + AppVersion;
        _loading = false;
        ShowOverlay(Loc.T("Settings"), SettingsPanel);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Cfg.DarkTheme = ThemeDark.IsChecked == true; Cfg.Save(); ApplyTheme();
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Cfg.Language = LangEn.IsChecked == true ? "en" : "de"; Loc.Lang = Cfg.Language; Cfg.Save();
        ApplyLanguage(); RebuildTiles(); RefreshEditor();
        Settings_Click(this, e);
    }

    private void Min_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Cfg.MinimizeToTrayOnly = MinTray.IsChecked == true; Cfg.Save();
    }

    private async void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool want = AutostartBox.IsChecked == true;
        AutostartBox.IsEnabled = false;
        bool ok = await Task.Run(() => SystemInfo.SetAutostart(want));
        bool now = await Task.Run(SystemInfo.AutostartEnabled);
        AutostartBox.IsChecked = now; AutostartBox.IsEnabled = true;
        if (!ok) MessageBox.Show(this, Loc.T("SetAutostartFail"), Loc.T("Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        FanController.Log("Autostart: " + (now ? "an" : "aus"));
    }

    private static readonly string AppVersion = (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)) ?? "?";

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConfig.Dir}\"") { UseShellExecute = true });

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        HelpContent.Children.Clear();
        foreach (var (head, body) in Loc.HelpSections)
        {
            HelpContent.Children.Add(new TextBlock { Text = head, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            var t = new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            HelpContent.Children.Add(t);
        }
        ShowOverlay(Loc.T("Help"), HelpPanel);
    }

    // ===================== Diagramm =====================
    private double W => Math.Max(10, Chart.ActualWidth - PadL - PadR);
    private double H => Math.Max(10, Chart.ActualHeight - PadT - PadB);
    private double X(double temp) => PadL + (temp - 20) / 80.0 * W;
    private double Y(double pct) => PadT + (1 - pct / 100.0) * H;

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        if (Chart.ActualWidth < 50) return;
        Chart.Children.Clear();
        var grid = Theme.B("Line"); var muted = Theme.B("Muted");
        for (int p = 0; p <= 100; p += 20)
        {
            Chart.Children.Add(new Line { X1 = PadL, X2 = PadL + W, Y1 = Y(p), Y2 = Y(p), Stroke = grid, StrokeThickness = 1 });
            AddLabel($"{p} %", 0, Y(p) - 8, muted);
        }
        for (int t = 20; t <= 100; t += 10)
        {
            Chart.Children.Add(new Line { X1 = X(t), X2 = X(t), Y1 = PadT, Y2 = PadT + H, Stroke = grid, StrokeThickness = 1, Opacity = 0.5 });
            AddLabel($"{t}°", X(t) - 10, PadT + H + 8, muted);
        }

        _area = new Polygon();
        _area.SetResourceReference(Shape.FillProperty, "CurveArea");
        _savedLine = new Polyline { Stroke = muted, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0.8 };
        _glow = new DropShadowEffect { Color = UiColors.CurveGlow, ShadowDepth = 0, BlurRadius = 14, Opacity = 0.8 };
        _line = new Polyline { StrokeThickness = 3.5, StrokeLineJoin = PenLineJoin.Round, Effect = _glow };
        _line.SetResourceReference(Shape.StrokeProperty, "CurveStroke");
        Chart.Children.Add(_area); Chart.Children.Add(_savedLine); Chart.Children.Add(_line);

        var nowBrush = Theme.B("Text");
        _nowLine = new Line { Stroke = nowBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 }, Opacity = 0.6 };
        _nowDot = new Ellipse { Width = 14, Height = 14, Fill = nowBrush, Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.6 } };
        _nowLbl = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = nowBrush };
        Chart.Children.Add(_nowLine); Chart.Children.Add(_nowDot); Chart.Children.Add(_nowLbl);

        for (int i = 0; i < 17; i++)
        {
            int idx = i;
            var el = new Ellipse
            {
                Width = 16, Height = 16, Fill = new RadialGradientBrush(Colors.White, Theme.C("Card")) { GradientOrigin = new Point(0.35, 0.3) },
                StrokeThickness = 3, Cursor = Cursors.SizeNS,
                Effect = new DropShadowEffect { ShadowDepth = 2, BlurRadius = 5, Opacity = 0.45, Direction = 270 }
            };
            el.SetResourceReference(Shape.StrokeProperty, "CurveStroke");
            el.MouseLeftButtonDown += (_, e) => { _drag = idx; _dragStart = _draft.ToArray(); Chart.CaptureMouse(); e.Handled = true; };
            el.MouseEnter += (_, _) => ShowTip(idx);
            el.MouseLeave += (_, _) => { if (_drag < 0 && _tip != null) _tip.Visibility = Visibility.Collapsed; };
            _pts[i] = el; Chart.Children.Add(el);
        }
        var tipText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        tipText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _tip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Visibility = Visibility.Collapsed, Effect = Theme.Shadow(3, 10), Child = tipText };
        _tip.SetResourceReference(Border.BackgroundProperty, "CardHover");
        Chart.Children.Add(_tip);
        UpdateCurve();
    }

    private void AddLabel(string text, double x, double y, Brush b)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, Foreground = b };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); Chart.Children.Add(tb);
    }

    private void UpdateCurve()
    {
        if (_line == null || _area == null || _savedLine == null) return;
        var pts = new PointCollection(Curve.Temps.Select((t, i) => new Point(X(t), Y(_draft[i]))));
        _line.Points = pts;
        _area.Points = new PointCollection(pts) { new Point(X(100), Y(0)), new Point(X(20), Y(0)) };
        var saved = EditCurve.Percent;
        _savedLine.Points = new PointCollection(Curve.Temps.Select((t, i) => new Point(X(t), Y(saved[i]))));
        _savedLine.Visibility = CurveDirty ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < 17; i++) { Canvas.SetLeft(_pts[i], X(Curve.Temps[i]) - 8); Canvas.SetTop(_pts[i], Y(_draft[i]) - 8); }
        UpdateNowMarker();
    }

    private void UpdateNowMarker()
    {
        if (_nowLine == null || _nowDot == null || _nowLbl == null || _work.Length != 3) return;
        var t = _app.Controller.Sensors.Get(_work[_sel].Source);
        if (t is not double temp) { _nowLine.Visibility = _nowDot.Visibility = _nowLbl.Visibility = Visibility.Collapsed; return; }
        _nowLine.Visibility = _nowDot.Visibility = _nowLbl.Visibility = Visibility.Visible;
        var tc = Math.Clamp(temp, 20, 100);
        var p = Curve.Evaluate(_draft, tc);
        _nowLine.X1 = _nowLine.X2 = X(tc); _nowLine.Y1 = PadT; _nowLine.Y2 = PadT + H;
        Canvas.SetLeft(_nowDot, X(tc) - 7); Canvas.SetTop(_nowDot, Y(p) - 7);
        _nowLbl.Text = Loc.T("Now", temp, p) + (CurveDirty || ProfileDirty ? " (" + Loc.T("Preview") + ")" : "");
        Canvas.SetLeft(_nowLbl, Math.Min(X(tc) + 8, PadL + W - 150)); Canvas.SetTop(_nowLbl, PadT + 2);
    }

    private void ShowTip(int i)
    {
        if (_tip == null) return;
        ((TextBlock)_tip.Child).Text = $"{Curve.Temps[i]} °C · {_draft[i]} %";
        Canvas.SetLeft(_tip, Math.Min(X(Curve.Temps[i]) + 12, PadL + W - 110)); Canvas.SetTop(_tip, Y(_draft[i]) - 34);
        _tip.Visibility = Visibility.Visible;
    }

    private void Chart_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag < 0 || _dragStart == null) return;
        int v = (int)Math.Round(Math.Clamp((1 - (e.GetPosition(Chart).Y - PadT) / H) * 100, 0, 100));
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int delta = v - _dragStart[_drag];
            for (int i = 0; i < 17; i++) _draft[i] = i >= _drag ? Math.Clamp(_dragStart[i] + delta, 0, 100) : _dragStart[i];
        }
        else
        {
            // Nachbarn mitschieben: rechts nie niedriger, links nie höher als der gezogene Punkt
            for (int i = 0; i < 17; i++)
                _draft[i] = i == _drag ? v : i > _drag ? Math.Max(_dragStart[i], v) : Math.Min(_dragStart[i], v);
        }
        UpdateCurve(); ShowTip(_drag); UpdateDirtyUi();
    }

    private void Chart_MouseUp(object sender, MouseEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1; _dragStart = null; Chart.ReleaseMouseCapture();
        if (_tip != null) _tip.Visibility = Visibility.Collapsed;
        UpdateDirtyUi();   // Speicherleiste jetzt nachziehen
    }
}
