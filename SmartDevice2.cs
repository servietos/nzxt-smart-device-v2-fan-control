// Ansteuerung NZXT Smart Device V2 / RGB & Fan Controller (HUE-2-Familie) über USB-HID.
// Protokoll nach liquidctl (GPL-3.0), liquidctl/driver/smart_device.py, Klasse SmartDevice2.
// Nur Lüfter: Status lesen (Drehzahl, Duty, Modus) und feste Duty je Kanal setzen.
using HidSharp;

namespace NzxtFan;

public sealed class FanState
{
    public int Rpm;
    public int Duty;
    public int Mode; // 0 = nicht angeschlossen, 1 = DC, 2 = PWM
}

public sealed class SmartDevice2 : IDisposable
{
    public const int VendorId = 0x1E71;
    // Produkt-IDs mit 3 Lüfterkanälen und gleichem Protokoll (siehe liquidctl _MATCHES)
    public static readonly int[] ProductIds = { 0x2006, 0x200D, 0x200F, 0x2009, 0x200E, 0x2010, 0x2011, 0x2019, 0x201F, 0x2020 };
    public const int FanCount = 3;

    private readonly HidDevice _device;
    private HidStream? _stream;
    private Thread? _reader;
    private volatile bool _running;
    private readonly object _writeLock = new();
    private readonly Action<string> _log;

    public FanState[] Fans { get; } = Enumerable.Range(0, FanCount).Select(_ => new FanState()).ToArray();
    public DateTime LastStatus { get; private set; } = DateTime.MinValue;
    public string Description => $"{_device.GetProductName()} (PID 0x{_device.ProductID:X4})";
    public string FirmwareVersion { get; private set; } = "?";

    private SmartDevice2(HidDevice device, Action<string> log) { _device = device; _log = log; }

    public static SmartDevice2? Find(Action<string> log)
    {
        var dev = DeviceList.Local.GetHidDevices(VendorId)
            .FirstOrDefault(d => ProductIds.Contains(d.ProductID) && d.GetMaxOutputReportLength() > 0);
        return dev == null ? null : new SmartDevice2(dev, log);
    }

    /// Öffnet das Gerät, aktiviert die Statusmeldungen und startet den Lesethread.
    public void Open(byte statusInterval = StatusNormal)
    {
        Close();
        _stream = _device.Open();
        _stream.ReadTimeout = 1000;
        _stream.WriteTimeout = 1000;   // Schreiben läuft im Takt der Oberfläche: ein hängendes Gerät darf sie nicht sekundenlang einfrieren
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "NzxtSmartDevice2Reader" };
        _reader.Start();
        Initialize(statusInterval);
    }

    /// Wie liquidctl initialize(): Statusintervall setzen, Lüfter erkennen, Firmware abfragen.
    /// Muss nach Systemstart und nach dem Aufwachen aus dem Standby erneut laufen.
    public void Initialize(byte statusInterval = StatusNormal)
    {
        SetStatusInterval(statusInterval);
        Write(0x60, 0x03);
        Write(0x10, 0x01);
    }

    public const byte StatusNormal = 1;    // 0,5 s wie liquidctl: 1 + (Sekunden − 0,5) / 0,25
    public const byte StatusQuiet = 0xFF;  // rund eine Minute: praktisch keine Statusmeldungen (gemessen: sofort wirksam)

    /// Abstand der Statusmeldungen des Controllers setzen (ohne erneute Lüftererkennung).
    public void SetStatusInterval(byte interval) => Write(0x60, 0x02, 0x01, 0xE8, interval, 0x01, 0xE8, interval);

    /// Feste Drehzahl in Prozent (0–100) für Kanal 0–2 setzen.
    public void SetDuty(int channel, int percent)
    {
        if (channel is < 0 or >= FanCount) throw new ArgumentOutOfRangeException(nameof(channel));
        percent = Math.Clamp(percent, 0, 100);
        var msg = new byte[] { 0x62, 0x01, (byte)(1 << channel), 0x00, 0x00, 0x00 };
        msg[channel + 3] = (byte)percent;
        Write(msg);
    }

    private void Write(params byte[] data)
    {
        var s = _stream ?? throw new InvalidOperationException("Gerät nicht geöffnet");
        var buf = new byte[_device.GetMaxOutputReportLength()];
        Array.Copy(data, buf, Math.Min(data.Length, buf.Length)); // Byte 0 = Report-ID (wie liquidctl)
        lock (_writeLock) s.Write(buf);
    }

    private void ReadLoop()
    {
        var buf = new byte[Math.Max(_device.GetMaxInputReportLength(), 64)];
        while (_running)
        {
            int n;
            try { n = _stream!.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch (Exception ex) { if (_running) _log($"Lesefehler: {ex.Message}"); Thread.Sleep(500); continue; }
            if (n < 2) continue;
            if (buf[0] == 0x67 && buf[1] == 0x02 && n >= 43) ParseFanStatus(buf);
            else if (buf[0] == 0x11 && buf[1] == 0x01 && n > 0x13) FirmwareVersion = $"{buf[0x11]}.{buf[0x12]}.{buf[0x13]}";
        }
    }

    private void ParseFanStatus(byte[] m)
    {
        for (int i = 0; i < FanCount; i++)
        {
            Fans[i].Mode = m[16 + i];
            Fans[i].Rpm = m[24 + 2 * i] | (m[25 + 2 * i] << 8);
            Fans[i].Duty = m[40 + i];
        }
        LastStatus = DateTime.UtcNow;
    }

    public void Close()
    {
        _running = false;
        try { _stream?.Dispose(); } catch { }
        _reader?.Join(1500);
        _stream = null; _reader = null;
    }

    public void Dispose() => Close();
}
