# Razer Blade Tray Controller

System tray app for Razer Blade 15 Mid 2019 (and similar) to switch performance modes without Synapse.

Controls power mode (Balanced/Gaming/Creator), fan speed, boost, and Windows power plan via USB HID.

## How It Works

Uses libusb-1.0.dll to send USB control transfers directly to the Razer Blade embedded controller — no Synapse, no Razer software needed.

## Features

- **Performance Mode**: Balanced, Gaming, Creator
- **Fan Speed**: 3500/4000/4500/5000 RPM presets, Auto (EC controlled), Custom (any value)
- **Boost Toggle**: On/Off (if supported by your model)
- **Temperatures**: CPU + GPU display in tray tooltip
- **Game Detection**: Automatically switches mode when a configured game/DAW is in the foreground
- **Windows Power Plan Sync**: Syncs the Windows power plan to match the selected mode
- **Dynamic Tray Icon**: Green (Balanced), Red (Gaming), Blue (Creator), Gray (unknown)
- **No Synapse required**: Works standalone after uninstalling Razer Synapse

## Game Detection Configuration

Edit `game-modes.config` in the same folder as `RazerTray.exe`:

```json
[
  {
    "ExeName": "Cyberpunk2077.exe",
    "Mode": 1,
    "FanRpm": 5000
  },
  {
    "ExeName": "FL64.exe",
    "Mode": 2,
    "FanRpm": 0,
    "AutoFan": true
  }
]
```

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `ExeName` | string | Executable name (e.g., `Cyberpunk2077.exe`, `BitwigStudio.exe`) |
| `Mode` | int | 0=Balanced, 1=Gaming, 2=Creator |
| `FanRpm` | int | Fan speed in RPM (0 = let the EC handle it with AutoFan) |
| `AutoFan` | bool | Let EC control fans automatically (default: false if FanRpm > 0) |
| `MatchPathContaining` | string (optional) | Partial path match for disambiguation (e.g., `\Steam\steamapps\common\`) |

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
