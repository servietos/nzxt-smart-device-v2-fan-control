using LibreHardwareMonitor.Hardware;

namespace NzxtFan;

/// Liest CPU- und GPU-Temperatur über LibreHardwareMonitor (CPU braucht PawnIO + Adminrechte).
public sealed class Sensors : IDisposable
{
    private readonly Computer _pc = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    public double? Cpu { get; private set; }
    public double? Gpu { get; private set; }
    public string CpuSensorName { get; private set; } = "-";

    public Sensors()
    {
        try { _pc.Open(); }
        catch (Exception ex) { FanController.Log("Sensoren konnten nicht geöffnet werden: " + ex.Message); }   // Regelung läuft dann mit Sicherheitsdrehzahl
    }

    public void Update()
    {
        double? cpu = null, gpu = null;
        foreach (var hw in _pc.Hardware)
        {
            hw.Update();
            var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125).ToList();
            if (hw.HardwareType == HardwareType.Cpu)
            {
                var s = temps.FirstOrDefault(x => x.Name == "CPU Package") ?? temps.FirstOrDefault(x => x.Name == "Core Max") ?? temps.OrderByDescending(x => x.Value).FirstOrDefault();
                if (s != null && cpu == null) { cpu = s.Value; CpuSensorName = s.Name; }
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                var s = temps.FirstOrDefault(x => x.Name == "GPU Core") ?? temps.FirstOrDefault();
                if (s != null && (gpu == null || hw.HardwareType != HardwareType.GpuIntel)) gpu = s.Value;
            }
        }
        Cpu = cpu; Gpu = gpu;
    }

    /// Nach einem Lesefehler: Werte als unbekannt markieren (Regelung fällt auf Sicherheitsdrehzahl).
    public void Invalidate() { Cpu = null; Gpu = null; }

    public double? Get(TempSource src) => src == TempSource.CPU ? Cpu : Gpu;

    public void Dispose() => _pc.Close();
}
