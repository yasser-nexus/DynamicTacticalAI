# GTA V Dynamic Tactical AI & Pursuit Overhaul

![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

A single-player GTA V PC script that adds varied police pursuit behavior, reactive civilians, and high-wanted-level roadblocks. Designed for the ScriptHookVDotNet **v3 API** and .NET Framework 4.8.

## Features

- **Pursuit:** periodically assigns nearby ambient police drivers to chase the player; at two or more stars, some instead drive toward an offset intercept point. Driver ability and aggression increase during pursuits.
- **Roadblocks:** at four or more stars, creates a parked police riot vehicle and a visible stinger on a nearby road ahead of the player. Models load asynchronously. The script simulates the stinger by bursting the front tires of the **player's vehicle** when its tracked path passes close enough; it does not affect other vehicles. This is a proximity approximation, not per-wheel contact detection. Roadblocks are capped and cleaned up when expired, far away, disabled, or the wanted level falls below four.
- **Bystanders:** nearby ambient pedestrians may seek cover or flee after the player fires, or flee from a speeding player vehicle. Some witnesses with line of sight use a phone; after four seconds, a living witness who remains close and on foot triggers a one-star wanted level if the player has none. The report can still complete if the animation is interrupted; it is not a full witness system.
- **Configuration:** feature toggles, hotkeys, intervals, radii, and limits via INI, with defaults for missing or invalid settings.
- **Safeguards:** bounded update batches, cooldowns, existence checks, nonblocking model requests, per-entity exception handling, throttled error logging, and cleanup of spawned entities. Mission-owned peds and vehicles are skipped when assigning ambient AI tasks.

Behavior is approximate: intercepts are independently calculated, not a dispatch simulation; roadblocks are unattended and may appear visibly or be poorly placed on unusual terrain. Police tasks, driver attributes, civilian tasks, popped tires and wanted levels are not restored when the script is disabled. This script cannot guarantee zero crashes: native calls and other mods can fail outside managed exception handling. Test against your particular game and hook versions.

## Requirements

- GTA V PC in **single-player/story mode**. Do not use script mods in GTA Online.
- A game-compatible [Script Hook V](http://www.dev-c.com/gtav/scripthookv/) and ASI loader.
- A compatible [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) release with the **v3** runtime installed, plus its stated prerequisites (including .NET Framework 4.8 and the required x64 Visual C++ runtime). Game updates can require newer hook builds.
- To build: a .NET SDK with .NET Framework targeting support, or Visual Studio/MSBuild with NuGet restore and the .NET Framework 4.8 developer pack. NuGet access is required.

## Build and installation

1. Install Script Hook V, its ASI loader, and ScriptHookVDotNet according to their upstream instructions. Ensure the v3 runtime is present and configured to use the default `scripts` directory.
2. To build from source, from the repository directory run `dotnet build DynamicTacticalAI.csproj -c Release`. The project restores the ScriptHookVDotNet3 3.6.0 compile-time API and framework reference assemblies from NuGet. Alternatively open the project in Visual Studio and build Release.
3. Create a `scripts` folder next to `GTA5.exe` if needed. Copy the built `bin/Release/net48/DynamicTacticalAI.dll` (or the included `release/DynamicTacticalAI.dll`) and `DynamicTacticalAI.ini` into that folder. Do **not** overwrite the ScriptHookVDotNet runtime DLL with a package copy.
4. Launch story mode. Press **F10** to toggle this script, and **F11** to reload the INI. Toggle and reload display a notification. Configure conflicting keys if necessary. Exceptions, when encountered, are rate-limited and written to `scripts/DynamicTacticalAI.log` where possible.
5. To uninstall, exit the game and remove the two installed files (and the optional log).

## Configuration

Edit `scripts/DynamicTacticalAI.ini`, then press F11. Lines beginning with `;` or `#` are comments; use `true` or `false` for booleans. Missing, malformed, or unreadable settings use defaults; numeric values are clamped to the ranges below. Decimal values use a period. Use plain [Windows Forms Keys](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.keys?view=netframework-4.8) names such as `F10` or `NumPad1`, not combinations. Avoid using the same key for both actions. Paths assume the default `scripts` directory relative to the game root.

| Section | Setting | Default | Range / meaning |
|---|---|---:|---|
| Features | Pursuit | true | Police pursuit changes; also gates roadblocks |
| Features | Bystanders | true | Civilian reactions; also gates reports |
| Features | Roadblocks | true | High-wanted roadblocks |
| Features | CivilianReports | true | Civilians can report player gunfire |
| Keys | Toggle | F10 | Toggle the script |
| Keys | Reload | F11 | Reload configuration |
| Tuning | PoliceIntervalMs | 2500 | 1000–30000 ms |
| Tuning | CivilianIntervalMs | 1800 | 1000–30000 ms |
| Tuning | RoadblockIntervalMs | 60000 | 10000–600000 ms between attempts |
| Tuning | MaxRoadblocks | 2 | 1–5 |
| Tuning | RoadblockLifetimeSeconds | 120 | 20–600 seconds |
| Tuning | PoliceRadius | 120 | 25–250 game units |
| Tuning | CivilianRadius | 60 | 10–120 game units |
| Tuning | RoadblockDistance | 100 | 65–200 game units ahead |
| Tuning | SpeedThresholdMps | 25 | 5–80 meters per second |

Changes take effect on subsequent ticks; existing assigned AI tasks are left to the game. Roadblocks are removed on the next maintenance tick when their feature is disabled. Other scripts that control the same NPCs or wanted level can conflict.

## License

[MIT](LICENSE). GTA V, Script Hook V, and ScriptHookVDotNet are separate projects and are not included or licensed by this repository.

## Verification and known limitations

The included DLL was built from this source against ScriptHookVDotNet3 3.6.0 and .NET Framework 4.8 with zero compiler warnings and errors. It has **not been tested inside GTA V** in this environment. Before publishing, test on your exact game and hook versions: pursuit with one and four stars, roadblock creation and removal, spike contact at low and high speed, witness interruption, hotkeys, config reload, script reload, and conflicts with other mods. Do not advertise it as crash-proof or as a tested release until those checks pass.
