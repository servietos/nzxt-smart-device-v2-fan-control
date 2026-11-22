# NzxtFan

A small Windows tray app that controls the fans of an **NZXT Smart Device V2** (USB `1E71:200D`) without NZXT CAM. Lighting is optional and goes through OpenRGB.

Kleine Windows-App für den Infobereich, die die Lüfter eines NZXT Smart Device V2 ohne NZXT CAM regelt. Die Oberfläche gibt es auf Deutsch und Englisch.

> Not affiliated with NZXT. Tested on exactly one PC (firmware 1.12.0, three fans). Use at your own risk.

## Features

- Fan curves by CPU or GPU temperature, with a curve editor and a shared curve library
- Profiles (fans and lighting together), light and dark theme, German and English
- Safety: fail-safe duty if a sensor is missing, minimum speed at high temperature, defined duty on exit
- Stall protection: a fan reporting 0 rpm gets a start-up kick and a higher minimum, otherwise a warning
- Lighting via an OpenRGB SDK server that the app starts hidden (fixed colour, temperature colour, rainbow)
- Autostart through a scheduled task, starts minimised to the tray

## Requirements

- Windows 10/11 x64, administrator rights (sensor access)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) (SDK to build)
- [PawnIO](https://pawnio.eu/) driver for the CPU temperature (without it, only the GPU temperature is available)
- Optional: [OpenRGB 1.1](https://openrgb.org/) for lighting, either in an `OpenRGB` folder next to `NzxtFan.exe` or chosen in the settings
- NZXT CAM must not run at the same time; it locks the device

## Build

```
dotnet publish -c Release -o publish
```

Then start `publish\NzxtFan.exe`. Settings and the log are stored in `%APPDATA%\NzxtFan`.

## Known limits

- The number of fan channels is fixed at three (what the Smart Device V2 has).
- OpenRGB 1.1 can misread the LED counts of this controller while status reports are running. The app pauses the reports while it starts OpenRGB. An OpenRGB server that is already running is used as it is.

## Sources and credits

- HID protocol: [liquidctl](https://github.com/liquidctl/liquidctl) (`smart_device.py`, GPL-3.0)
- Lighting: [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) and its SDK protocol documentation
- Sensors: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0)
- USB access: [HidSharp](https://www.zer7.com/software/hidsharp) (Apache-2.0)

The code was written with the help of an AI assistant (Claude Code) and tested by the author on their own hardware.

## License

GPL-3.0, see [LICENSE](LICENSE).
