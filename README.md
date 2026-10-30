# DiplomacyPlux

Mods for [Diplomacy is Not an Option](https://store.steampowered.com/app/1272320/Diplomacy_is_Not_an_Option/), built as [BepInEx 5](https://github.com/BepInEx/BepInEx) plugins.

## Veteran Levels

Your units gain experience from kills and level up into named veterans.

- **Experience:** a unit gets XP for each enemy it kills. Each level needs more XP than the last, up to level 10.
- **Bonuses per level:** +5% damage (melee and ranged), +5% max health, and +1% model size.
- **Names:** at their first level-up, units get a name, then a title for each level: *"Aldric the Blooded"* … *"Aldric the Immortal"*.
- **Hero cap:** a unit becomes a hero when it first levels up. You can have 5 heroes; at the cap, other units don't gain XP until a hero dies. Press F8 to open the hero panel, where you can raise the cap to 10 (10 soul crystals) and then 15 (20 soul crystals).
- **Promotion:** units in a promotion chain become the next unit type when they reach level 5, starting again at level 0 with a full heal and keeping their name. The last unit type in a chain levels up to 10. Default chains:
  - Spearman → Swordsman → Knight
  - Archer → Crossbowman
  - Horseman → Cavalier → Dark Knight

  Units in a tower or building wait until they leave. Units outside the chains (siege, healers) don't promote and level up to 10.
- **Tamed creatures:** research Beast Taming in the hero panel (F8, 25 soul crystals) and the Barracks can train wolves (1 population) and Heffalumps (4 population), each costing 1 food for now while testing, under your control. Before the research their Barracks buttons show as locked.
- **Soul crystals:** a unit reaching level 5 earns you 2 soul crystals, and level 10 earns 5 more.
- **Enemy heroes:** about 1 in 30 enemy wave units spawns as a hero at level 2, with the same per-level bonuses. Heroes gain XP by killing your units and level up the same way. They show a red label and XP bar (hidden in the fog of war), and an announcement says when heroes join an attack and when one falls.
- **On screen:** a name label and XP bar above each veteran, plus an announcement when a unit levels up.
- **Info panel:** the selected unit's damage stat includes its veteran bonus (shown as increased). When several units are selected, the bonus is shown only if they all share the same level.
- **Saves:** levels survive save/load.

| Key | Action |
|---|---|
| F8 | Open/close the hero panel (hero count, cap upgrades, research) |
| F9 | Show/hide veteran labels and XP bars |
| F10 | Give every selected unit one level (cheat) |
| F11 | Drop 50 wood and 50 stone at the mouse cursor; walk a unit over the piles to collect (cheat) |

Every value (XP per kill, level curve, max level, bonuses, enemy hero chance and starting level, soul crystal rewards, hero cap and upgrade costs, promotion level and chains, trainable creatures and their costs, keys) can be changed in `BepInEx/config/trystan.dino.veteranlevels.cfg`. Restart the game after editing it.

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
- **Info panel damage:** a Harmony patch on `InfoPanel.UpdateParametersAndStatuses` swaps in the boosted damage while the panel builds its stats, then restores the game's value.
- **Size** comes from a `Scale` component, which the transform system folds into the unit's world matrix.
- **Enemy heroes** are rolled when a wave unit spawns (while it still has `WaveUnitDisabled` and is walking in from the map edge). Waves already give enemies their own health multiplier, so the hero bonus is multiplied on top of it.
- **Soul crystals** are added to the game's `CurrentSouls` resource.
- **Tamed creatures** are added to the game's Barracks shop list (`GameState.BarracksShopStates`), so they get native training buttons, prices and queues. The research lock is the game's own `ResearchObjectLock` tag on the creature prefab. Creature prefabs are built as enemies, so the mod removes `Enemy` from trained ones (trained units carry a `Citizen` component, wild ones don't), including after a save is loaded.
- **Hero cap upgrades** are stored in `CurrentSouls.value`, a legacy field the game still saves but only reads from very old saves, so they survive save/load without a separate file. Researches are stored there too.
- **Promotion** spawns the next unit type's prefab where the old unit stands and moves over what the game itself moves when it converts units: citizen data, home, extra population slots (ghost citizens), hunger/cold, squad flags and mission-trigger references. The old unit is then destroyed. Promoted units add 0.001 to their health multiplier per promotion, so they are still recognized as heroes after loading at level 0.

### Known limitations

- **Kill credit** is a best guess when several units attack the same enemy.
- **Saves:** XP toward the next level and unit names are not saved. Loaded veterans keep their level but get a new name.
- **`HealthPerLevel`:** changing it mid-campaign changes the levels decoded from existing saves.
- **Enemy heroes and saves:** hero status isn't saved. After loading, a hero keeps its extra health but loses its label, damage bonus and leveling. Enemy units still walking in from the map edge when a save is loaded are rolled again.
- **Promotion** keeps the original unit's population cost and doesn't check research or army limits for the new unit type. Formation membership isn't carried over. Veterans above level 5 from saves made before promotion existed promote when the save loads.
- **Enemy hero kill credit** uses the same best guess as player kills.

## Repo layout

- `VeteranLevels/`: plugin source.
- `tools/install.sh`: installs BepInEx and deploys the plugin.
- `tools/backup-before-edit.sh`: Claude Code hook that backs up original game files before they are edited.

No game files, decompiled game code or third-party binaries are included.
