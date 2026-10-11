CYBERBULL-E: play Schedule I as Jimmy Hopkins
=============================================

A fan-made, non-commercial mod for Schedule I (PC, Steam). Jimmy Hopkins replaces your character, with Bully's
controls, weapon wheel, prank kit, fist combos, bikes / moped / go-kart, voice lines and wardrobe, and the town's
adults are played by Bully's adults.

BRING YOUR OWN BULLY
--------------------
This download contains NOTHING from Bully: no models, textures, sounds or animations. The launcher builds them on
your PC from YOUR OWN installed copy of Bully: Scholarship Edition. You need to own and install both games.

Not affiliated with or endorsed by Rockstar Games, Take-Two Interactive or TVGS. Bully and its characters belong to
Rockstar Games; Schedule I belongs to TVGS.

WHAT YOU NEED
-------------
  * Schedule I (Steam), on the normal (default) branch
  * Bully: Scholarship Edition, installed (Steam or Rockstar Launcher)
  * MelonLoader 0.7.x installed into Schedule I  (https://github.com/LavaGang/MelonLoader/releases)
    Run Schedule I once after installing MelonLoader so it can finish setting up, then close it.
  * Windows 10/11, 64-bit, and the .NET 8 Desktop Runtime (Windows offers to install it the first time if missing)
  * About 150 MB of free space in the Schedule I folder

INSTALL
-------
  1. Unzip this folder anywhere (keep the "payload" folder next to "CyberBull-E Launcher.exe").
  2. Run "CyberBull-E Launcher.exe".
  3. Check the status lines. If Bully isn't found, click "Find Bully..." and pick the folder with Bully.exe in it.
  4. Click INSTALL + PLAY. The first time, it builds the Bully files from your copy (about 3 to 5 minutes), installs
     the mod and starts Schedule I through Steam.

After that, you can start the game from Steam as usual. Run the launcher again after updating CyberBull-E.

KEYS
----
  F6   the mod on / off (off = stock Schedule I)      (gamepad: L3 + R3)
  F8   first / third person
  F10  save diagnostics (for bug reports)
Ride, shop and wardrobe controls are shown on screen when you use them.

WHERE THINGS GO
---------------
The mod's own files are named BullyLink (its working name), so that's what you'll see in the game folder.
  Schedule I\Mods\BullyLink.dll                  the mod
  Schedule I\UserData\BullyLink\data, peds, clothes   built from your Bully (never shared, never uploaded)
  Schedule I\UserData\BullyLink\outfits          your saved outfits
The mod never touches either game's save files or Bully's install folder (it only reads Bully).

UNINSTALL
---------
Launcher -> Uninstall. "Yes" removes the mod and the built Bully files; "No" removes just the mod.

TROUBLE?
--------
  * The game starts without Jimmy and says to run the launcher: the Bully files weren't built. Click Install.
  * The export failed: the details are in Schedule I\UserData\BullyLink\export.log. Please include it in bug reports.
  * Anything else: include Schedule I\MelonLoader\Latest.log and Schedule I\UserData\BullyLink.trace.txt.
  * Antivirus warnings about "CyberBull-E Exporter.exe": it's a Python program packed with PyInstaller, which some
    scanners flag by habit. It only reads your two game folders and writes into Schedule I\UserData\BullyLink.
