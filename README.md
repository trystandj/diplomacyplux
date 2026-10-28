# DiplomacyPlux

Mods for [Diplomacy is Not an Option](https://store.steampowered.com/app/1272320/Diplomacy_is_Not_an_Option/), built as [BepInEx 5](https://github.com/BepInEx/BepInEx) plugins.

## Veteran Levels

Your units gain experience from kills and level up into named veterans.

- **Experience:** a unit gets XP for each enemy it kills. Each level needs more XP than the last, up to level 10.
- **Bonuses per level:** +25% damage (melee and ranged), +25% max health, and +3% model size.
- **Names:** at their first level-up, units get a name, then a title for each level: *"Aldric the Blooded"* … *"Aldric the Immortal"*.
- **On screen:** a name label and XP bar above each veteran, plus an announcement when a unit levels up.
- **Saves:** levels survive save/load.

| Key | Action |
|---|---|
| F9 | Show/hide veteran labels and XP bars |
| F10 | Give every selected unit one level (cheat) |

Every value (XP per kill, level curve, max level, bonuses, keys) can be changed in `BepInEx/config/trystan.dino.veteranlevels.cfg`. Restart the game after editing it.

### Install

1. Install **BepInEx 5.4.x (win x64)** into the game folder: extract the zip next to `Diplomacy is Not an Option.exe`.
2. Copy `VeteranLevels.dll` to `BepInEx/plugins/VeteranLevels/`.
3. Start the game. `BepInEx/LogOutput.log` should show `Veteran Levels ... loaded.`

`tools/install.sh` automates both steps for the author's setup. It only adds files and refuses to overwrite game files. Edit its paths for your machine.

To uninstall, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder from the game folder.

### Build

Requires the .NET SDK (6 or newer) and the game installed.

```
cd VeteranLevels
dotnet build -c Release
```

The project references the game's DLLs from `Diplomacy is Not an Option_Data/Managed` (the `GameDir` property in `VeteranLevels.csproj`). It takes `BepInEx.dll` from the game's `BepInEx/core`, or from `VeteranLevels/lib/BepInEx/core/` if BepInEx isn't installed yet. Output: `VeteranLevels/bin/Release/VeteranLevels.dll`.

### How it works

The game runs on Unity DOTS (ECS) with Burst-compiled combat jobs, which Harmony can't patch. The mod instead adds its own ECS system, which runs each frame before the game's fight and health systems:

- **Melee hits** are `HealthChangeRequest` entities, and a veteran's damage is scaled up before it is applied.
- **Arrows and other projectiles** carry `ProjectileFlyData.damage`, which is scaled up when the projectile is created.
- **Kill credit:** the game doesn't record who dealt a hit. A hit that brings an enemy to 0 HP is credited to the nearest player unit attacking that enemy.
- **Max health** uses each unit's own `HealthBase` multiplier. The game already saves it, so levels are rebuilt from it after loading.
- **Size** comes from a `Scale` component, which the transform system folds into the unit's world matrix.

### Known limitations

- **Kill credit** is a best guess when several units attack the same enemy.
- **Saves:** XP toward the next level and unit names are not saved. Loaded veterans keep their level but get a new name.
- **`HealthPerLevel`:** changing it mid-campaign changes the levels decoded from existing saves.

## Repo layout

- `VeteranLevels/`: plugin source.
- `tools/install.sh`: installs BepInEx and deploys the plugin.
- `tools/backup-before-edit.sh`: Claude Code hook that backs up original game files before they are edited.

No game files, decompiled game code or third-party binaries are included.
