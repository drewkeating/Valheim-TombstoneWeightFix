# TombstoneWeightFix

BepInEx plugin for Valheim. Fixes ExtraSlots' tombstone auto-loot check for heavy loot (lightened-row discount, belt upgrades, the temporary Corpse Run buff) and adds a sorted pickup for tombstones that are too heavy: gear, hotbar, then the heaviest items first, up to your carry weight. See `package/README.md` for what it does and its settings.

## Build
Needs Valheim and a BepInEx install (a Gale/r2modman profile works) for reference assemblies, and a C# compiler (Visual Studio Build Tools Roslyn `csc.exe`).

```powershell
./build.ps1                 # builds dist/TombstoneWeightFix.dll
./build.ps1 -Package        # also builds dist/Scooby_Drew902-TombstoneWeightFix-<version>.zip (Thunderstore layout)
./build.ps1 -ValheimDir 'D:\Steam\steamapps\common\Valheim' -BepInExDir '<profile>\BepInEx'
```

## Layout
- `src/TombstoneWeightFix.cs` - the whole plugin
- `package/` - manifest.json, README.md, CHANGELOG.md, icon.png for publishing
- `build.ps1` - build + package script

Plugin ID: `scooby_drew902.valheim.tombstoneweightfix`.

ExtraSlots and OtMegingjord are reached by reflection (nothing is compiled against them), so the plugin builds without them and does nothing if their internals change.

## License
GPL-3.0. See `LICENSE`.
