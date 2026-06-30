# Razer Blade Tray Controller

System tray app for Razer Blade 15 Mid 2019 (and similar) to switch performance modes without Synapse.

[![Build](https://github.com/cloph-dsp/razer-blade-tray/actions/workflows/build.yml/badge.svg)](https://github.com/cloph-dsp/razer-blade-tray/actions/workflows/build.yml)

Controls power mode (Balanced/Gaming/Creator), fan speed, CPU/GPU temps, Windows power plan, NVIDIA GPU profile, and process priority — all via USB HID to the embedded controller.

No Synapse, no Razer software, no bloat.

## How It Works

Uses `libusb-1.0.dll` to send USB control transfers directly to the Razer Blade embedded controller, using the packet format reverse-engineered from [librazerblade](https://github.com/Meetem/librazerblade).

## Features

- **Performance Mode**: Balanced, Gaming, Creator (USB + Windows power plan + NVIDIA GPU profile)
- **Fan Control**: Temperature-based auto fan curve, manual RPM presets, or EC auto mode
- **CPU/GPU Monitoring**: Real-time temperatures, GPU clock/MHz/utilization in tray tooltip
- **Game Detection**: Auto-switches mode + fan profile when a configured game/DAW is foreground
- **Process Management**: Auto-set priority (realtime/high/normal), I/O priority, CPU affinity, disable power throttling for DAWs and games
- **Auto Fan Curve**: Temperature-based RPM targeting with per-mode offsets
- **NVIDIA NVAPI Integration**: GPU power management and per-game DRS profiles
- **Boost Toggle**: On/Off (if supported by your model)
- **Auto-Start**: Registry Run key or Scheduled Task (run at logon with highest privileges)
- **System Tweaks**: One-click MMCSS, timer resolution, network tweaks
- **Dynamic Tray Icon**: Green (Balanced), Red (Gaming), Blue (Creator), Gray (disconnected)
- **Sleep/Resume**: Preserves state across sleep/wake cycles

## Quick Start

1. [Download the latest release](https://github.com/cloph-dsp/razer-blade-tray/releases)
2. Extract `RazerTray.exe` + `libusb-1.0.dll` to a folder
3. Run as Administrator (required for USB access)
4. Right-click the lightning bolt icon in the system tray
5. Optionally enable **Run at Startup** from the menu

### USB Access

The app requires Administrator privileges for libusb control transfers. Two startup options:
- **Run at Startup**: Registers in HKCU Run key (works, but UAC prompt may appear at logon)
- **Install Scheduled Task** (recommended): Runs at logon with highest privileges, no UAC prompt

## Game Detection Configuration

Copy `game-modes.config.example` to `game-modes.config` and edit:

```json
[
  {
    "name": "Cyberpunk 2077",
    "exe": "Cyberpunk2077.exe",
    "mode": "Game Mode",
    "fanSpeed": 5000,
    "priority": "high",
    "ioPriority": "high",
    "cpuAffinity": null,
    "noPowerThrottling": true,
    "gpuProfile": { "gpuPowerMode": 1, "optimusRenderingMode": 2 }
  },
  {
    "name": "Bitwig Studio",
    "exe": "Bitwig Studio.exe",
    "mode": "Audio Mode",
    "fanSpeed": 3500,
    "priority": "realtime",
    "ioPriority": "high",
    "cpuAffinity": "0,2-11",
    "noPowerThrottling": true
  }
]
```

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Display name |
| `exe` | string | Executable name (e.g., `Cyberpunk2077.exe`, `Bitwig Studio.exe`) |
| `mode` | string | `"Game Mode"` or `"Audio Mode"` |
| `fanSpeed` | int | Fan speed in RPM (0 = EC auto) |
| `priority` | string | Process priority class: `realtime`, `high`, `above normal`, `normal`, `below normal`, `idle` |
| `ioPriority` | string | I/O priority: `high`, `normal`, `low`, `very low` |
| `cpuAffinity` | string | CPU cores (e.g., `"0,2-11"` skips core 1, `null` = all cores) |
| `noPowerThrottling` | bool | Disable Windows Efficiency Mode for this process |
| `gpuProfile` | object | NVIDIA GPU profile override (optional) |

### Built-in Process Rules

The app applies these automatically (no config needed):

| Process | Priority | I/O | Affinity | Throttling |
|---------|----------|-----|----------|------------|
| Bitwig Studio | Realtime | High | Skip core 1 | Disabled |
| Bitwig Audio Engine | High | High | Skip core 1 | Disabled |
| Bitwig Plugin Host | High | High | All | Disabled |
| audiodg.exe | High | High | All | Disabled |

### How to add a game

1. Launch the game
2. Check the exe name via Task Manager → Details tab
3. Add an entry to `game-modes.config`
4. Save the file and click **Reload Config** in the tray menu

The app checks the foreground window every 2 seconds. When the game is detected, it:
- Switches to the configured power mode
- Sets the fan speed (or leaves it on auto)
- Shows "Game Mode Active" in the tooltip

To restore manual control, click any mode in the menu or switch modes manually.

## Compiling from Source

Requires .NET Framework 4.x SDK (csc.exe):

```
csc.exe /target:winexe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Web.Extensions.dll RazerTray.cs
```

## Files

| File | Purpose |
|------|---------|
| `RazerTray.exe` | Compiled binary |
| `RazerTray.cs` | C# source code |
| `libusb-1.0.dll` | USB HID transport DLL |
| `game-modes.config` | Game detection rules |

## Supported Models

Tested on **Razer Blade 15 Mid 2019 (RZ09-03009W76, PID=0x0246)**.

Other Razer Blade models with librazerblade support should work with the correct PID. Edit the `VID`/`PID` constants in the source if needed.
