# NetLedger

A lightweight internet usage monitor for Windows 11, in the spirit of NetworX:

- **Live speeds on the taskbar**: upload and download, with an optional mini graph, sitting right next to the tray icons.
- **History**: usage per minute, hour, day, week, month and year, per adapter, in a statistics window.
- **Per-app usage**: which programs used how much, kept per hour forever.
- **Monthly quota**: a data cap with a custom billing day, notifications at chosen percentages, and a projection.
- **Outage log**: when the internet dropped and for how long.
- **Backup & restore**: the whole history in one file, with undo.

## How it works

NetLedger is two programs:

| Part | Runs as | Does |
|---|---|---|
| `NetLedger.Service.exe` | Windows service (LocalSystem) | Reads adapter byte counters every second, attributes traffic to apps, checks connectivity, and stores everything in SQLite under `%ProgramData%\NetLedger` |
| `NetLedger.exe` | You, at sign-in | Draws the taskbar widget, the statistics and settings windows, and quota notifications |

They talk over a local named pipe. The live taskbar speeds are measured by `NetLedger.exe` itself, so the widget keeps working even if the service is stopped. The service is only needed for history.

Some details worth knowing:

- **Adapter totals** come from Windows' own interface counters (`GetIfTable2`), the same numbers Task Manager uses. Only physical adapters count toward the internet total by default. VPN tunnels and virtual switches carry traffic that also crosses a physical adapter, so counting them too would double it. You can change this per adapter in Settings → Adapters.
- **Per-app usage** comes from the kernel's network event trace (`Microsoft-Windows-Kernel-Network` ETW provider), which reports the process and payload size of every TCP and UDP send and receive. That requires administrator rights, which is why recording lives in a service.
  - It counts payload only, so per-app totals land a little under the adapter totals.
  - Traffic the kernel handles itself (such as file sharing) shows up as "System".
- **Times** are stored in UTC and grouped in local time when shown, so daylight-saving changes come out right.
- **Outages** are detected by fetching `http://www.msftconnecttest.com/connecttest.txt` once a minute (every 10 seconds while offline). That's the same check Windows uses for its "No internet" indicator. The address and interval can be changed, or the check turned off, in Settings → Connectivity.
- **The taskbar widget** is a transparent child window inside the Windows 11 taskbar, since Windows 11 has no API for taskbar toolbars. If Windows ever stops allowing that, switch Settings → Taskbar → Placement to *Floating*.

## Install

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it. The installer checks for it.
2. Run `NetLedger-Setup-<version>.exe`. It asks for administrator rights once, to register the service.

Uninstalling removes the service. It asks whether to keep your recorded history.

## Using it

- **Hover** the taskbar readout for today's and this month's totals.
- **Click** it to open statistics. Click a bar (or double-click a row) to drill down from year → month → day → hour → minute. ← and → step between periods.
- **Right-click** it for settings, backup and restore.
- Running `NetLedger.exe` again while it's already running opens the statistics window.

## Build from source

Requires the .NET 10 SDK. [Inno Setup 6](https://jrsoftware.org/isinfo.php) is optional and only needed for the installer.

```powershell
dotnet build NetLedger.slnx
dotnet test --project tests/NetLedger.Tests
.\scripts\publish.ps1            # publish to artifacts\publish\win-x64 and build the installer
```

For development, the service can run in a console with its own data folder:

```powershell
src\NetLedger.Service\bin\Debug\net10.0-windows\NetLedger.Service.exe --console --data .\devdata
```

Per-app tracking needs an elevated console. To register the published build as a real service without the installer, run `scripts\install-dev.ps1` from an elevated prompt.

## Project layout

```
src/NetLedger.Core      shared: interface counters, period maths, formatting, pipe protocol, backup format
src/NetLedger.Service   the Windows service: collection, ETW, outage checks, SQLite storage, pipe server
src/NetLedger.Tray      the WinForms app: taskbar widget, statistics, settings, notifications
tests/NetLedger.Tests   unit tests (DST handling, billing cycles, counters, storage, import/export)
installer/              Inno Setup script
scripts/                publish, dev install and icon scripts
```

## Privacy

Everything stays on your PC. The only network request NetLedger makes is the connectivity check described above, and you can turn it off. The pipe the tray app uses refuses connections from other computers. The service's data folder is readable only by SYSTEM and administrators.

## License

[MIT](LICENSE)
