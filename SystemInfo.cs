using System.Diagnostics;
using Microsoft.Win32;

namespace NzxtFan;

/// Prüfungen und Einstellungen auf Systemebene: PawnIO-Treiber, Autostart, NZXT CAM.
public static class SystemInfo
{
    /// Der Treiber PawnIO wird für die CPU-Temperatur gebraucht.
    public static bool PawnIoInstalled()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return k != null; }
        catch { return false; }
    }

    public static bool CamRunning() => Process.GetProcessesByName("NZXT CAM").Length > 0;

    /// Lichtdienst von Armoury Crate („ASUS AURA SYNC lighting service“).
    private const string AuraService = "LightingService";

    public static bool AuraServiceRunning()
    {
        try { using var p = Process.Start(new ProcessStartInfo("sc.exe", $"query {AuraService}") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return o.Contains("RUNNING"); }
        catch { return false; }
    }

    /// Stoppt den Aura-Lichtdienst und stellt ihn auf manuell (rückgängig: sc config LightingService start= auto).
    public static bool DisableAuraService()
    {
        int a = Run("sc.exe", $"stop {AuraService}"), b = Run("sc.exe", $"config {AuraService} start= demand");
        FanController.Log($"Aura-Lichtdienst gestoppt ({a}) und auf manuell gestellt ({b}).");
        return b == 0;
    }

    private static int Run(string exe, string args)
    {
        try { using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!; p.WaitForExit(15000); return p.ExitCode; }
        catch (Exception ex) { FanController.Log(exe + ": " + ex.Message); return -1; }
    }

    private const string TaskName = "NzxtFan";

    /// Autostart über die Aufgabenplanung (mit höchsten Rechten, damit keine UAC-Abfrage beim Anmelden kommt).
    public static bool AutostartEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"").code == 0;

    /// Legt die Aufgabe als XML an. Die Kurzform „schtasks /SC ONLOGON“ reicht nicht: Windows beendet solche Aufgaben
    /// nach 72 Stunden hart (die Lüfter blieben dann ungeregelt stehen) und startet sie mit niedriger Priorität.
    public static bool SetAutostart(bool on)
    {
        if (!on) return RunSchtasks($"/Delete /TN \"{TaskName}\" /F").code == 0;
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NzxtFan-autostart.xml");
        try
        {
            System.IO.File.WriteAllText(file, AutostartXml(Environment.ProcessPath!), System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F").code == 0;
        }
        catch (Exception ex) { FanController.Log("Autostart: " + ex.Message); return false; }
        finally { try { System.IO.File.Delete(file); } catch { } }
    }

    private static string AutostartXml(string exe)
    {
        var user = System.Security.SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exe)}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// Beim Programmstart: eine vorhandene Autostart-Aufgabe erneuern, wenn sie noch auf eine andere Programmdatei zeigt
    /// (z. B. einen älteren Programmordner) oder noch die 72-Stunden-Grenze hat.
    public static void RepairAutostart()
    {
        var (code, xml) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        if (code != 0) return;   // kein Autostart eingerichtet
        var exe = System.Security.SecurityElement.Escape(Environment.ProcessPath!)!;
        if (xml.Contains(exe, StringComparison.OrdinalIgnoreCase) && xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>")) return;
        FanController.Log(SetAutostart(true) ? "Autostart-Aufgabe erneuert: " + Environment.ProcessPath : "Autostart-Aufgabe konnte nicht erneuert werden.");
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
            var readOut = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return (-1, ""); }
            return (p.ExitCode, readOut.Wait(2000) ? readOut.Result : "");
        }
        catch (Exception ex) { FanController.Log("schtasks: " + ex.Message); return (-1, ""); }
    }
}
