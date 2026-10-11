# CyberBull-E (source)

Play Schedule I as Jimmy Hopkins. The mod for players is on Nexus Mods: https://www.nexusmods.com/schedule1/mods/2826

**Bring your own Bully.** Nothing from Bully: Scholarship Edition is in this repository or in the release. The exporter
(`tools/`) rebuilds every Bully-derived file on the player's own PC from their own installed copy, into
`Schedule I/UserData/BullyLink`. The mod's files use its working name, BullyLink.

## What's here
| Path | What |
|---|---|
| `*.cs`, `BullyLink.csproj` | the MelonLoader mod (C#, net6.0, IL2CPP Schedule I) |
| `launcher/` | CyberBull-E Launcher (WinForms, .NET 8): finds both games, runs the exporter, installs, launches |
| `tools/` | the exporter (Python): reads Bully's archive / models / animations / sounds and Schedule I's level data, writes the mod's data files. `byob_export.py` is the entry point, packed with PyInstaller as `CyberBull-E Exporter.exe` |
| `assets/public/bullylink` | the effects asset bundle: Unity particle prefabs + URP shaders with placeholder dot textures (no Bully pixels); the mod paints the player's own textures on at runtime |
| `unity/BuildBully.cs` | the Unity editor script that builds that bundle (Unity 2022.3.62f2, URP 14) |
| `release/` | `make_release.py` (builds the zip), `check_release.py` (fails the build if anything Bully-derived or machine-specific is inside), the player README |

## Build
Prerequisites: Windows, .NET 8 SDK, Python 3.11 (`pip install numpy pillow UnityPy pyinstaller`), and Schedule I with
MelonLoader 0.7.x installed and run once (its generated IL2CPP assemblies are the build references). If Schedule I isn't in
Steam's default library, create `local.props` next to the csproj:
`<Project><PropertyGroup><GameDir>D:/path/to/Schedule I</GameDir></PropertyGroup></Project>`

```
python release/make_release.py
```
builds the mod (`dotnet build -c Release`, deterministic), the launcher (`dotnet publish`), the exporter (PyInstaller,
folder build), stages them, runs the leak check and writes `release/CyberBull-E-<version>.zip`.

Optional: rebuild the effects bundle in Unity 2022.3.62f2 with URP: put `unity/BuildBully.cs` in `Assets/Editor`, run
`python tools/make_placeholders.py Assets/Bully/Tex`, then
`Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod BuildBully.Build`.

## What the programs do on a player's PC
- **Exporter:** reads the Bully and Schedule I install folders (never writes to them), writes only to
  `Schedule I/UserData/BullyLink/`. No network access.
- **Launcher:** reads Steam's library list from the registry, runs the exporter, copies `BullyLink.dll` into
  `Schedule I/Mods/`, starts the game through `steam://`, reads MelonLoader's log to confirm the load. Its only web link
  opens MelonLoader's GitHub releases page if MelonLoader is missing.
- **Mod:** reads `UserData/BullyLink` and Bully's speech bank; writes its trace, settings and saved outfits under `UserData`.

Not affiliated with or endorsed by Rockstar Games, Take-Two Interactive or TVGS. Source published for transparency and
review; all rights reserved (permissions as listed on the Nexus page).
