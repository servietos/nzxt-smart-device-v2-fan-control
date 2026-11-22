// Minimaler Client für das OpenRGB-SDK (TCP, standardmäßig 127.0.0.1:6742).
// Protokoll laut OpenRGB-Dokumentation „OpenRGB SDK Documentation“; verwendet wird Protokollversion 3
// (Modi mit Helligkeit, Zonen ohne Segmente), die alle OpenRGB-Versionen ab 0.6 verstehen.
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace NzxtFan;

public sealed class RgbMode
{
    public int Index;
    public string Name = "";
    public int Value;
    public uint Flags, SpeedMin, SpeedMax, BrightnessMin, BrightnessMax, ColorsMin, ColorsMax, Speed, Brightness, Direction, ColorMode;
    public uint[] Colors = Array.Empty<uint>();

    // Flags laut OpenRGB (RGBController.h): Speed=1<<0, Richtung=1<<1..1<<3, Helligkeit=1<<4, je LED=1<<5, modusspezifisch=1<<6, Zufall=1<<7
    public bool HasSpeed => (Flags & (1 << 0)) != 0;
    public bool HasDirection => (Flags & 0b1110) != 0;
    public bool HasBrightness => (Flags & (1 << 4)) != 0;
    public bool HasPerLedColor => (Flags & (1 << 5)) != 0;
    public bool HasModeSpecificColor => (Flags & (1 << 6)) != 0;
    public bool HasRandomColor => (Flags & (1 << 7)) != 0;
    public bool IsDirect => Name.Equals("Direct", StringComparison.OrdinalIgnoreCase);
    public bool IsOff => Name.Equals("Off", StringComparison.OrdinalIgnoreCase);
    /// Farbmodus: 0 keine, 1 je LED, 2 modusspezifisch, 3 zufällig
    public bool UsesModeColors => ColorMode == 2 && ColorsMax > 0;
}

public sealed class RgbZone
{
    public int Index;
    public string Name = "";
    public int Type;
    public uint LedsMin, LedsMax, LedsCount;
}

public sealed class RgbDevice
{
    public int Index;
    public int Type;
    public string Name = "", Vendor = "", Description = "", Version = "", Serial = "", Location = "";
    public int ActiveMode;
    public List<RgbMode> Modes = new();
    public List<RgbZone> Zones = new();
    public int LedCount;
    /// Stabiler Schlüssel zum Wiedererkennen über Neustarts hinweg.
    public string Key => $"{Name}|{Location}";
}

public sealed class OpenRgbClient : IDisposable
{
    private const uint ProtocolVersion = 3;
    private const uint ReqControllerCount = 0, ReqControllerData = 1, ReqProtocolVersion = 40, SetClientName = 50,
        UpdateLeds = 1050, UpdateZoneLeds = 1051, SetCustomMode = 1100, UpdateMode = 1101;

    private TcpClient? _tcp;
    private NetworkStream? _s;
    private readonly object _lock = new();
    public uint ServerProtocol { get; private set; }
    public bool Connected => _tcp?.Connected == true;

    public void Connect(string host = "127.0.0.1", int port = 6742, int timeoutMs = 1500)
    {
        Dispose();
        _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 5000, SendTimeout = 5000 };
        if (!_tcp.ConnectAsync(host, port).Wait(timeoutMs)) throw new IOException("OpenRGB-Server antwortet nicht");
        _s = _tcp.GetStream();
        lock (_lock)
        {
            Send(0, ReqProtocolVersion, BitConverter.GetBytes(ProtocolVersion));
            var (_, data) = Receive(ReqProtocolVersion);
            ServerProtocol = BitConverter.ToUInt32(data, 0);
            Send(0, SetClientName, Encoding.ASCII.GetBytes("NzxtFan\0"));
        }
    }

    private uint Proto => Math.Min(ProtocolVersion, ServerProtocol);

    public List<RgbDevice> GetDevices()
    {
        lock (_lock)
        {
            Send(0, ReqControllerCount, Array.Empty<byte>());
            var (_, cnt) = Receive(ReqControllerCount);
            int n = (int)BitConverter.ToUInt32(cnt, 0);
            var list = new List<RgbDevice>();
            for (int i = 0; i < n; i++)
            {
                Send((uint)i, ReqControllerData, BitConverter.GetBytes(Proto));
                var (_, data) = Receive(ReqControllerData);
                var d = ParseController(data, Proto);
                d.Index = i;
                list.Add(d);
            }
            return list;
        }
    }

    /// Schaltet das Gerät in den „Direct/Custom“-Modus (Software setzt die Farben).
    public void SetDirectMode(int dev) { lock (_lock) Send((uint)dev, SetCustomMode, Array.Empty<byte>()); }

    public void SetZoneColors(int dev, int zone, IReadOnlyList<uint> colors)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(0u); w.Write((uint)zone); w.Write((ushort)colors.Count);
        foreach (var c in colors) w.Write(c);
        var b = ms.ToArray(); BitConverter.GetBytes((uint)b.Length).CopyTo(b, 0);
        lock (_lock) Send((uint)dev, UpdateZoneLeds, b);
    }

    public void SetAllLeds(int dev, IReadOnlyList<uint> colors)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(0u); w.Write((ushort)colors.Count);
        foreach (var c in colors) w.Write(c);
        var b = ms.ToArray(); BitConverter.GetBytes((uint)b.Length).CopyTo(b, 0);
        lock (_lock) Send((uint)dev, UpdateLeds, b);
    }

    /// Aktiviert einen Hardware-Modus (Effekt) mit den gegebenen Einstellungen.
    public void SetMode(int dev, RgbMode m)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(0u); w.Write(m.Index);
        WriteString(w, m.Name);
        w.Write(m.Value); w.Write(m.Flags); w.Write(m.SpeedMin); w.Write(m.SpeedMax);
        if (Proto >= 3) { w.Write(m.BrightnessMin); w.Write(m.BrightnessMax); }
        w.Write(m.ColorsMin); w.Write(m.ColorsMax); w.Write(m.Speed);
        if (Proto >= 3) w.Write(m.Brightness);
        w.Write(m.Direction); w.Write(m.ColorMode);
        w.Write((ushort)m.Colors.Length);
        foreach (var c in m.Colors) w.Write(c);
        var b = ms.ToArray(); BitConverter.GetBytes((uint)b.Length).CopyTo(b, 0);
        lock (_lock) Send((uint)dev, UpdateMode, b);
    }

    // ---------- Paketebene ----------
    private void Send(uint dev, uint id, byte[] payload)
    {
        var s = _s ?? throw new IOException("nicht verbunden");
        var hdr = new byte[16];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(hdr, 0);
        BitConverter.GetBytes(dev).CopyTo(hdr, 4);
        BitConverter.GetBytes(id).CopyTo(hdr, 8);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(hdr, 12);
        s.Write(hdr); if (payload.Length > 0) s.Write(payload);
    }

    /// Liest Pakete, bis eines mit der erwarteten ID kommt (Benachrichtigungen wie „Geräteliste geändert“ werden übersprungen).
    private (uint dev, byte[] data) Receive(uint expectId)
    {
        var s = _s ?? throw new IOException("nicht verbunden");
        while (true)
        {
            var hdr = ReadExact(s, 16);
            if (Encoding.ASCII.GetString(hdr, 0, 4) != "ORGB") throw new IOException("ungültige Antwort von OpenRGB");
            uint dev = BitConverter.ToUInt32(hdr, 4), id = BitConverter.ToUInt32(hdr, 8), size = BitConverter.ToUInt32(hdr, 12);
            var data = size > 0 ? ReadExact(s, (int)size) : Array.Empty<byte>();
            if (id == expectId) return (dev, data);
            if (id == 100) DeviceListChanged = true;
        }
    }

    /// Wird gesetzt, wenn OpenRGB meldet, dass sich die Geräteliste geändert hat.
    public bool DeviceListChanged { get; set; }

    private static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n]; int o = 0;
        while (o < n) { int r = s.Read(b, o, n - o); if (r <= 0) throw new IOException("Verbindung zu OpenRGB getrennt"); o += r; }
        return b;
    }

    // ---------- Gerätedaten ----------
    private static RgbDevice ParseController(byte[] data, uint proto)
    {
        var r = new BinaryReader(new MemoryStream(data));
        r.ReadUInt32();                                   // Gesamtgröße
        var d = new RgbDevice { Type = r.ReadInt32(), Name = ReadString(r) };
        if (proto >= 1) d.Vendor = ReadString(r);
        d.Description = ReadString(r); d.Version = ReadString(r); d.Serial = ReadString(r); d.Location = ReadString(r);
        int numModes = r.ReadUInt16();
        d.ActiveMode = r.ReadInt32();
        for (int i = 0; i < numModes; i++)
        {
            var m = new RgbMode { Index = i, Name = ReadString(r), Value = r.ReadInt32(), Flags = r.ReadUInt32(), SpeedMin = r.ReadUInt32(), SpeedMax = r.ReadUInt32() };
            if (proto >= 3) { m.BrightnessMin = r.ReadUInt32(); m.BrightnessMax = r.ReadUInt32(); }
            m.ColorsMin = r.ReadUInt32(); m.ColorsMax = r.ReadUInt32(); m.Speed = r.ReadUInt32();
            if (proto >= 3) m.Brightness = r.ReadUInt32();
            m.Direction = r.ReadUInt32(); m.ColorMode = r.ReadUInt32();
            int nc = r.ReadUInt16(); m.Colors = new uint[nc];
            for (int k = 0; k < nc; k++) m.Colors[k] = r.ReadUInt32();
            d.Modes.Add(m);
        }
        int numZones = r.ReadUInt16();
        for (int i = 0; i < numZones; i++)
        {
            var z = new RgbZone { Index = i, Name = ReadString(r), Type = r.ReadInt32(), LedsMin = r.ReadUInt32(), LedsMax = r.ReadUInt32(), LedsCount = r.ReadUInt32() };
            int matrixLen = r.ReadUInt16();
            if (matrixLen > 0) r.ReadBytes(matrixLen);    // Matrixdaten (Höhe, Breite, Zuordnung) werden nicht gebraucht
            d.Zones.Add(z);
        }
        int numLeds = r.ReadUInt16();
        for (int i = 0; i < numLeds; i++) { ReadString(r); r.ReadUInt32(); }
        d.LedCount = numLeds;
        return d;
    }

    private static string ReadString(BinaryReader r)
    {
        int len = r.ReadUInt16();
        var b = r.ReadBytes(len);
        return Encoding.ASCII.GetString(b, 0, Math.Max(0, len - 1));   // ohne abschließende Null
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var b = Encoding.ASCII.GetBytes(s + "\0");
        w.Write((ushort)b.Length); w.Write(b);
    }

    /// Farbe im OpenRGB-Format: Bytes R, G, B, 0 (little endian → 0x00BBGGRR).
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public void Dispose()
    {
        try { _s?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _s = null; _tcp = null;
    }
}
