using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace NzxtFan;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private WinForms.NotifyIcon? _tray;
    private DispatcherTimer? _timer;
    internal AppConfig Config = null!;
    internal FanController Controller = null!;
    internal bool PawnIo;
    internal LightingEngine Lighting = null!;
    private MainWindow? _win;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "NzxtFan.SingleInstance", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "NzxtFan.ShowWindow");
        if (!first)
        {
            // Läuft schon (z. B. im Infobereich): dort das Fenster öffnen lassen statt still zu beenden
            _showSignal.Set();
            Shutdown(); return;
        }
        new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow); }) { IsBackground = true, Name = "ShowSignal" }.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, a) => { FanController.Log("Absturz: " + a.ExceptionObject); try { Controller?.Dispose(); } catch { } };
        DispatcherUnhandledException += (_, a) => { FanController.Log("Fehler: " + a.Exception); a.Handled = true; };

        Config = AppConfig.Load();
        Theme.Apply(Config.DarkTheme);
        PawnIo = SystemInfo.PawnIoInstalled();
        FanController.Log(PawnIo ? "PawnIO-Treiber vorhanden." : "PawnIO-Treiber fehlt, CPU-Temperatur nicht verfügbar.");
        Controller = new FanController(Config, new Sensors());
        Lighting = new LightingEngine(Controller.Sensors, Controller);
        Lighting.Configure(Config.LightingEnabled, Config.OpenRgbPath, Config.LightDevices, Config.Active.Lights);
        UpdateAccent(force: true);
        FanController.Log($"Gestartet, Profil '{Config.ActiveProfile}'" + (Config.ImportedFrom != null ? $", importiert aus {Config.ImportedFrom}" : ""));

        new Thread(SystemInfo.RepairAutostart) { IsBackground = true, Name = "AutostartCheck" }.Start();

        SystemEvents.PowerModeChanged += (_, a) => { if (a.Mode == PowerModes.Resume) { Dispatcher.Invoke(Controller.Resume); Lighting.Refresh(); } };

        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = Loc.T("Title"),
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add(Loc.T("Open"), null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add(Loc.T("Exit"), null, (_, _) => ExitApp());
        _tray.DoubleClick += (_, _) => ShowWindow();
        // Hinweise auch dann zeigen, wenn das Programm unsichtbar im Infobereich startet
        if (!PawnIo) _tray.ShowBalloonTip(10000, Loc.T("Title"), Loc.T("PawnMissing"), WinForms.ToolTipIcon.Warning);
        Controller.FanStalled += ch => _tray?.ShowBalloonTip(15000, Loc.T("Title"), Loc.T("FanStalled", Config.FanName(ch)), WinForms.ToolTipIcon.Warning);
        AppConfig.SaveFailed += msg => Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(8000, Loc.T("Title"), Loc.T("SaveFailed", msg), WinForms.ToolTipIcon.Error));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            Controller.Tick();
            if (_tray != null)
            {
                var t = Controller.Sensors;
                var txt = $"{Loc.T("Title")} · CPU {Fmt(t.Cpu)} · GPU {Fmt(t.Gpu)}";
                _tray.Text = txt.Length > 63 ? txt[..63] : txt;
            }
            UpdateAccent(force: false);
            if (_win is { IsVisible: true }) _win.Refresh();
        };
        _timer.Start();
        Controller.Tick();

        if (!e.Args.Contains("--tray")) ShowWindow();
    }

    private static string Fmt(double? v) => v is double d ? $"{d:0} °C" : "–";

    internal void ShowWindow()
    {
        _win ??= new MainWindow(this);
        _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
        _win.Refresh();
    }

    internal bool Exiting => _exiting;

    private (byte, byte, byte)? _lastAccent;

    /// Akzentfarbe der Oberfläche: fest gewählt oder live von einer Beleuchtungszone übernommen.
    internal void UpdateAccent(bool force)
    {
        (byte r, byte g, byte b) c = Rgb.Parse(Config.AccentColor);
        if (Config.AccentFromLight && Lighting.ShownColor(Config.AccentDevice, Config.AccentZone) is { } lc) c = lc;
        if (!force && _lastAccent == c) return;
        _lastAccent = c;
        Theme.SetAccent(System.Windows.Media.Color.FromRgb(c.r, c.g, c.b));
        _win?.AccentChanged();
    }

    internal void UpdateTrayTexts()
    {
        if (_tray?.ContextMenuStrip == null) return;
        _tray.ContextMenuStrip.Items[0].Text = Loc.T("Open");
        _tray.ContextMenuStrip.Items[1].Text = Loc.T("Exit");
    }

    internal void ExitApp()
    {
        _exiting = true;
        _timer?.Stop();
        Controller.Dispose();
        Config.Save();   // verzögert gespeicherte Einstellungen (Farben, Namen) nicht verlieren
        try { Lighting.Dispose(); } catch { }
        Controller.Sensors.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _win?.Close();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (!_exiting) ExitApp();
        base.OnSessionEnding(e);
    }
}
