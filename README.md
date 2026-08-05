<div align="center">

# Archon

Run multiple Roblox game clients on Windows and launch saved accounts directly into a game.

[![Last commit](https://img.shields.io/github/last-commit/Relshy/archon?style=flat-square)](https://github.com/Relshy/archon/commits/main)
[![Commit activity](https://img.shields.io/github/commit-activity/w/Relshy/archon?style=flat-square)](https://github.com/Relshy/archon/commits/main)
[![Contributors](https://img.shields.io/github/contributors/Relshy/archon?style=flat-square)](https://github.com/Relshy/archon/graphs/contributors)
[![Code size](https://img.shields.io/github/languages/code-size/Relshy/archon?style=flat-square)](https://github.com/Relshy/archon)
[![License](https://img.shields.io/github/license/Relshy/archon?style=flat-square)](https://github.com/Relshy/archon/blob/main/LICENSE)

![Platform](https://img.shields.io/badge/platform-Windows-lightgrey?style=flat-square)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.0%2B-512BD4?style=flat-square)

</div>

## Overview

Archon allows multiple Roblox game clients to run at the same time on Windows.

The project provides two interfaces:

- **`MultiInstance.ps1`** — a console-based PowerShell script that enables multi-instance support and displays a live client count. No compilation is required.
- **`Archon.exe`** — a graphical launcher with instance monitoring, saved-account launching, system tray controls, and per-client performance settings.

`Launch.bat` provides a menu for building and running both interfaces.

Roblox files are never modified, patched, or injected into.

## Features

### Multi-instance control

- Can be enabled or disabled without restarting the launcher
- Supports starting the launcher before or after Roblox
- Does not modify or inject into Roblox files
- Creates the named objects with an access control entry for `Everyone`
- Applies a low-integrity label when the process has permission to do so, allowing medium-integrity Roblox clients to open the objects when the launcher is elevated

### Instance monitoring

Available in the GUI:

- Displays the PID, memory usage, uptime, and CPU usage of each client
- Displays a CPU usage sparkline for each client
- Identifies clients as:
  - `Running` — the process has an active window
  - `Starting` — the process is running but does not have a window yet
  - `Leftover` — the client window closed but the process is still running
- Focuses a selected client window
- Ends leftover processes after confirming the exact PIDs
- Detects both:
  - `RobloxPlayerBeta`
  - `Windows10Universal`

### Account launching

Available in the GUI:

- Stores a `.ROBLOSECURITY` cookie for each saved account
- Encrypts saved cookies with Windows DPAPI using the `CurrentUser` scope
- Exchanges a cookie for a single-use authentication ticket
- Launches an account directly into a Roblox place
- Joins public servers by place ID
- Joins private servers using a share link or access code

> [!WARNING]
> A `.ROBLOSECURITY` cookie grants access to its associated Roblox account. Never share it, include it in logs, or commit it to the repository.

### Performance controls

Available in the GUI:

- Configures the Roblox frame-rate cap through `DFIntTaskSchedulerTargetFps`
- Writes the frame-rate setting to Roblox's `ClientAppSettings.json`
- Reapplies the setting after the Roblox bootstrapper resets the file
- Configures the CPU priority class for each client
- Limits the logical processors available to each client

## Requirements

| Requirement | Details |
|---|---|
| Operating system | Windows 10 or Windows 11 |
| .NET Framework | 4.0 or later |
| PowerShell | 5.1 or later, required for `MultiInstance.ps1` |
| Administrator privileges | Only required to release handles from an already-running Roblox client |

Starting Archon before opening Roblox does not require administrator privileges.

The GUI is compiled using the C# compiler included with the installed .NET Framework:

```text
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

## Attribution

Made by [@sillyrelshy](https://github.com/Relshy)

- TikTok: [@sillyrelshy](https://www.tiktok.com/@sillyrelshy)
- Discord: @sillyrelshy
- GitHub: [Relshy/archon](https://github.com/Relshy/archon)

## License

Archon is licensed under the [MIT License](LICENSE).
