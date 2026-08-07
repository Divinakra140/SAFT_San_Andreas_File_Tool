<img width="1449" height="216" alt="cropped tab4" src="https://github.com/Divinakra140/SAFT_San_Andreas_File_Tool/blob/main/assets/Title%20wiith%20clouds.PNG" />

*Developed by Divinakra, August 2026*

- **[SAFT](https://github.com/Divinakra140/SAFT_San_Andreas_File_Tool/releases/download/v1.0/SAFT.exe)** is a portable 32-bit Windows tool compatible with any build or version of the PC game: GTA San
  Andreas, only the originals v1-v3, not the Definitive Edition.
- SAFT also runs well in certain PC emulators on Android — to be clear, that means SAFT modifies the 
  original PC game and SAFT can launch and function inside an emulated Windows container on Android. 
  SAFT cannot modify the Android port of GTA SA, only PC. 
- SAFT contains no Game assets, and only allows for users to modify their own GTA SA Game with their own mods.
- SAFT is a file extractor, identifier and replacer; it knows where every modified file goes, based on name.

Tested extensively against a real Steam release of GTA San Andreas (v3.0, specifically the "newsteam r2"
December 2014 patch — all 8 archives, 61,993 sound effects, and 1,922 streamed tracks verified
byte-for-byte). SAFT reads the game's own archive and audio config files directly rather than assuming
fixed file layouts, so it should work with any PC version of the classic GTA San Andreas.

**⚠️ IMPORTANT: SAFT does NOT work with GTA: The Trilogy – Definitive Edition.** Even though it has
"San Andreas" in the name, the Definitive Edition (released 2021) is a completely different, separate
game — it runs on entirely different technology than the classic PC version, so none of its files match
what SAFT knows how to read. If the game you own is called "Grand Theft Auto: San Andreas – The
Definitive Edition," SAFT will not work for you, no matter what you try.

---

## Legal Disclaimer & Notice

This software utility SAFT is an independent, community-driven project.
It is not affiliated with, authorized, maintained, sponsored, or endorsed by Rockstar Games,
Take-Two Interactive Software, Inc., RenderWare, or any of their affiliates or subsidiaries.

"Grand Theft Auto", "San Andreas", and all associated logos, names, and indicia are registered
trademarks of Take-Two Interactive Software, Inc. All other product or company names mentioned
are trademarks of their respective owners.

This tool is distributed strictly for educational, research, and non-commercial single-player
modification purposes. The developers do not distribute, host, or reproduce copyrighted game assets.

Users assume all liability and risk associated with the use of this software.

---

## SAFT Summary and Use-Case

SAFT is ideal for people who want to runs mods but do not want to downgrade their game to the
famous version 1.0 also known as the "hoodlum" version, which is not as polished as newer updated
versions of Rockstar's PC game, GTA San Andreas. SAFT is not meant for any other games other than GTA
San Andreas but SAFT is meant for any and all versions of that one Game, and only on one system: PC.

SAFT is also ideal for those who don't want to mess with mod loaders, mod managers or Cleo. Mod loaders
— and especially Cleo — can break compatibility with PC emulators. This is a
traditional, clean and lean file tool, and your game directory wont have any extra files in it, if
you don't want it to. With SAFT mods, GTA SA can look and play like a vanilla new copy of GTA SA, and
will run more stable in PC emulation on Android handhelds for example, even though the whole game
could be completely modified and different than stock, it runs like stock. Think of a Mod Loader like
a turbocharger and think of a SAFT like installing new engine parts on a naturally aspirated car. SAFT
modifies the game from the inside-out, whereas mod loaders modify the game from the outside-in.

SAFT is like the mechanic who understands your car very well, and can quickly and cleanly install any
new engine parts you want to bring to him as long as they fall into the 11 categories:

1. Models (.dff)
2. Collision (.col)
3. Textures (.txd)
4. Animations (.ifp)
5. Audio (.wav for SFX, .ogg for music)
6. Map Data (.ipl, .ide)
7. Paths (nodes .dat, .rrr car recordings)
8. Data Tables (.dat, .cfg, .zon, .ped, .grp)
9. Text (.gxt)
10. Cutscenes (.cut, .mpg)
11. Particle Effects (.fxp)

And as long as they are named correctly.

This tool does not contain any games or mods, it's just a file replacer, as long as you bring your own
GTA San Andreas PC game, and some mods, this tool can do all the work of replacing the game's vanilla
files with your modded files. SAFT scans your whole San Andreas PC Game directory and as long as your 
mod files are named exactly the same as the vanilla game files they aim to replace, SAFT will scan your
whole mod folder, and pick all the replaceable files and will replace them in your game directory. There 
are options built into SAFT that will automatically back up your vanilla files, to anywhere you tell it
to, so maybe make a folder called "backups" and tell it to back everything up there before you modify 
some files that you regret later. That way if you don't want the mods anymore,you can just use SAFT to
replace the modded files back with the vanilla files using the 5th "Uninstall Mod(s)" tab.

SAFT also has an extraction tool built into it, and can extract your whole GTA San Andreas Game into
an organized folder structure with all the individually named vanilla game files for you to manually
replace as you please. SAFT also has a tool that allows you to rebuild the game from whatever is
currently in your extracted folder. The extracted folder is bigger than the game folder. So only
extract if you have some free space available. SAFT will warn you about storage requirements. 
SAFT will not extract the audio archives by default becuase of how much more storage and time it 
takes to extract all the audio files, so if you are an audio mod dev, you can choose to also extract
the audio with the "Extract Audio As well?" check box. 

Most people who are not mod developers and are mod users and gamers will want to use the fourth option 
from the right called "Install Mod(s) without Extraction"  then leave the first bullet selected: 
"Backup original files before replacing (reccomended)" as this will enable future clean mod uninstalls.
<img width="1399" height="374" alt="cropped tab4" src="https://github.com/Divinakra140/SAFT_San_Andreas_File_Tool/blob/main/assets/Winlator%20screenshot%20Cropped.png" />
This is the most destructive tool contained within SAFT and allows you to replace files instantaneously
within your game directory without extracting anything (or backing anything up if you dont want to). This
works very fast as long as you don't try to replace with any huge files. If SAFT notices that the mod 
files you are providing are bigger in size than the vanilla files they replace, it will tell you that
the archive needs rebuilding. You can tell it no, and stop the file replacement before it happens, or 
you can say, "yes" and it will still work just as seamlessly as with small files, but it will take a 
bit longer. Thats because SAFT is not only replacing a file, its gotta make room for your big files 
and rebuild the archive around it. The "archive" its referring to is the "gta3.img" or "player.img" 
that all the individual game files and assets are contained within.

SAFT is a windows exe that is designed to also run in certain windows PC emulators. The two that
are currently known to work are [Winlator Official](https://github.com/brunodev85/winlator/releases/tag/v11.1.0) and [Winlator Bionic Vanilla](https://github.com/StevenMXZ/Winlator-Ludashi/releases/tag/v3.1.h). For
more detailed settings for those emulators that work see the "Emulator Settings" section of this Readme.

Note* SAFT works with mods that use Cleo to load their assets, as long as the assets are named the same
as they are named in the original game. SAFT will permanently load those mod files into your directory
and will ignore cleo files. However, some cleo-loaded files are not replacements and are additive and
for such files, SAFT cannot do anything with them. You can try to add them in manually to your game
directory if you know how to do that. SAFT only knows how to replace files already in the game.

---

## User Guide

1. Open up the SAFT folder and double click SAFT.exe.

2. Decide what you want to do, do you want to quickly replace some files? Or do you want to extract
   your whole game and manually sort through files replacing them manually and then rebuilding the game
   from that extracted folder?

3. For quick replacement of files. Simply find the mod(s) you want online and unzip the .zip archive
   to see the full folder structure of the mod. That main mod folder is what you want to provide to SAFT.

4. Then click "Install mod(s) Without Extraction"

5. Then browse for your "Game Folder" where the gta-sa.exe is located, and select that folder that
   contains your gta-sa.exe as the "Game Folder". (it doesn't matter what your game .exe is named,
   gta_sa.exe or gta-sa.exe or ahksgwhdiuh.exe will all work both to mod and launch the game)

6. Then for "Mod Folder", browse to the mod folder you downloaded and unzipped in step 3.

7. Then decide whether you want your vanilla game files backed up or not. If you want them backed up,
   than leave the top option selected "Backup original files before replacing" and tell it where you
   want your vanilla files backed up to. If you already have a backup elsewhere, or don't want a backup,
   click the second option: "Replace files without backups".

8. Then hit "Install Mod into Game files" and you should see some loading screens. Thats it!

9. If you get a window that tells you that your mod is bigger than the game files and the archive
   needs to be rebuilt, then just rebuild it, there's nothing lost here, that's normal and expected, and
   SAFT needs to make room for your bigger mods. Otherwise the modded files will simply now live in your
   game directory in place of the vanilla files. Your done. It's really that easy. Launch game normally.
   
10. If you want to uninstall a mod or a list of mods, you must have already backed up the original game
    files into distinct backup folders, for each mod or groups of mods that you might want uninstalled.
	Then simply use the 5th tab from the right as you have used all the others, whatever vanilla game
	files it finds in your backup folder, it will put back into your game directory where they belong, 
	getting rid of the currently loaded mod files. If you want to uninstall all your mods, just keep 
	all your vanilla game files backed up in one big folder and then select that as your backup folder.

11. If you are someone who wants to extract the whole game, and manually replace files, and rebuild it
    afterwards, simply use the first three tabs in that order "extract" "install" then "rebuild". You get
    the same result as someone who uses the fourth option, but you will be more intimately connected
    with the names of the files and how they are laid out. Mod developers will often want to use this
    option, whereas gamers and mod-users will usually want to use the fourth faster option. If you are a
    mod dev and want to modify the audio, make sure to click the option under extraction to "extract
    audio files as well". This is unselected by default because it takes forever to extract the whole
    game including audio. If you are a casual gamer, just make sure your mod folder has audio in a
    nested folder structure and not just loose .wav and .ogg files, as audio is the only file that needs
    nested folders to be id'd by SAFT.

---

## Acceptable File Types and Names

All mod files must be named identically to the games files for SAFT to replace them.

SAFT only accepts the native GTA SA file types, no .DAE for models, no mp3 for audio... ect..

1. **Models:** `.dff` (use DragonDFF plugin for blender)
2. **Collision:** `.col`
3. **Textures:** `.txd`
4. **Animations:** `.ifp`
5. **Audio:** mono 16-bit `.wav` for SFX and `.ogg` for Music (both are built in export options from audacity)
6. **Map Data:** `.ipl` (where every object sits in the world) and `.ide` (what each object IS — its
   model, texture and draw distance). This is what map mods are made of.
7. **Paths:** `nodes*.dat` — the invisible road and pavement network that vehicles and
   pedestrians navigate on — plus `.rrr` car recordings. A car recording is a pre-recorded vehicle
   route: a sequence of position, rotation and speed samples played back when a scripted vehicle
   has to drive an exact path (mission vehicles, the train on its track, planes on set flight
   paths, cutscene traffic). Replacing one changes the route and speed that vehicle takes.
8. **Data Tables:** `handling.cfg` (vehicle handling), `weapon.dat`, `carcols.dat`, `carmods.dat`,
   `.zon` map zones, `.ped` / `.grp` pedestrian behaviour, and the rest of the `data/` tables.
9. **Text:** `.gxt` — every subtitle, mission caption and menu string in the game, which means full
   translations can be installed with SAFT.
10. **Cutscenes:** `.cut` cutscene data, and the `.mpg` intro movies.
11. **Particle Effects:** `.fxp` — explosions, fire, smoke, water spray, muzzle flashes and the rest
    of the game's particle system.

That covers essentially every asset in the game. To put numbers on it, a stock v3 install holds
15,334 models, 3,983 textures, 435 animations, 426 car recordings, 251 collision files, 190 map
placement files and 212 data tables inside the archives, plus another 398 files sitting loose in the
game folder — SAFT can replace all of them.

### What SAFT will NOT replace

| Type | Why not |
|---|---|
| `.exe` | The game executable itself. SAFT replaces game assets, not the game. |
| `.dll` / `.asi` | Libraries and plugins — that's modloader/CLEO territory, which SAFT exists to avoid. |
| `.img` | Whole archives. SAFT works *inside* these; swapping one wholesale would bypass everything it does. |
| `.scm` | Game scripts — `data/script/main.scm` and the scripts inside `script.img`. See below. |

The script rule matters, so it's worth explaining. **SAFT only installs what it can uninstall.**
Every other file it touches is self-contained: put the original back and the change is completely
undone. Scripts are not. San Andreas save files are written against the script's global variable
layout, and they also store a table of references pointing at the scripts inside `script.img` *by
position*. Replace a script and an existing save can end up pointing into code that no longer
matches it. Saves live in `Documents\GTA San Andreas User Files`, outside your game folder, so no
backup SAFT makes can undo that. A broken save stays broken.

So SAFT refuses, and tells you when your mod folder contains one. If you still want it, `main.scm`
is a single unarchived file at `data > script > main.scm` — drag your modded copy over it yourself.
Everything else in that mod still installs normally.

### ~ Audio ~

Audio is a little more complicated than the rest in terms of naming and size:

- GTA SA Audio files don't have unique names, so they can't be identified by SAFT via name.
- Instead, audio files need to be properly nested in folders which tell SAFT what file they correspond
  to. Most if not all audio mods come in this file format anyways, so if you're a gamer, don't worry
  about it.

For Mod developers:

For Sound Effects: The the folder structure is (for example):
`Audio > SFX > GENRL > BANK_022 > Sound_001.wav`

For Music: the folder structure is (for example):
`Audio > Streams > AA > Track_001.ogg`

NOTE* music and SFX cannot be any bigger than the vanilla song or SFX file size. Only same or smaller is
Accepted by SAFT and by GTA SA and your audio file will remain vanilla if attempting to replace with a
Larger .wav or .ogg file size than the vanilla file you are replacing.

---

## Emulator Settings

*Tested only on Retroid Pocket Flip 2 (SD865).*

### Running SAFT

Tested and verified to work on [Winlator Official 11.1 Final by Brunodev](https://github.com/brunodev85/winlator/releases/tag/v11.1.0).

Tested and verified to work on [Winlator Bionic Vanilla 3.1 Hotfix by StevenMXZ](https://github.com/StevenMXZ/Winlator-Ludashi/releases/tag/v3.1.h).

- **Box64 preset:** Stability
- **Startup Selection:** Any, even Aggressive works.
- **Wine Version:** default wine settings on official winlator. for stevenmxz, see line directly below.
- **Wine Version (Bionic):** Proton-9.0-x86_64-0 (this is box, not fex).
- **DXVK Version:** 2.3.1 worked in Bionic and 2.4.1 worked in Official (default for both).
- **GPU driver:** default for both; newest mxz turnip 26.2.0 R7-Oneui was also used and worked.
- **Screen size:** can be reduced to 940 x 544 to see text more clearly, works at larger resolutions
  too. In fact, **960 x 544 is the recommended screen size** to use SAFT for ease and highest speed
  while keeping all features visible and clickable.

### Running GTA-SA (v3, 2014 Steam)

Tested and verified to work with the most competitive speeds and quality on 5 different apps — to make
this simple, only the best one is included here.

**[Winlator Star 11-2 Stable Build by Jacojayy](https://github.com/winhub-emu/winhub/releases/tag/star.11-2)** (1080p at stable 60FPS, full range: 50-100FPS).

- **Box64 Preset:** Performance
- **Startup Selection:** Aggressive
- **Wine version:** default (comes with box not fex)
- **DXVK version:** 1.10.3
- **GPU Driver:** Turnip (default) 25.1.0 (if you are not on a SD865 maybe try other drivers.)
- **Screen size/Resolution:** 1920 x 1080

**Game Display Advanced Settings:**
- Frame Limiter: OFF
- Widescreen: On
- Visual FX Quality: Low

Note* for archiving and preservation purposes, all three android emulator .apk files have been bundled
into the "Compatible_Emulators.ZIP" in the [releases page](https://github.com/Divinakra140/SAFT_San_Andreas_File_Tool/releases)
of the Github to download.

Links to download them from the original authors are here:

- [Official Winlator 11.1](https://github.com/brunodev85/winlator/releases/tag/v11.1.0)
- [Bionic Vanilla 3.1](https://github.com/StevenMXZ/Winlator-Ludashi/releases/tag/v3.1.h)
- [Winlator Star 11-2](https://github.com/winhub-emu/winhub/releases/tag/star.11-2)

---

## How SAFT works

GTA San Andreas (PC) ships its models, textures, collision, animations,
cutscenes, and minigame scripts inside VER2-format .img archives — a
directory table followed by sector-aligned (2048-byte), UNCOMPRESSED
concatenated files. The app has five tabs:

1. **Extract Game Files** — recursively finds every .img file under the chosen
   install folder (verifying the VER2 magic before treating it as an
   archive, so it isn't hardcoded to one release's exact layout), pulls
   every entry out into `<destination>/<archive's relative path>/<extension>/<filename>`,
   and writes a manifest.saft.json recording each archive's original file
   order. Extraction also mirrors every OTHER file in your game folder
   (loose textures like hud.txd, the .exe, movies, data files, etc.) into
   the destination as-is, so your extracted folder is a complete workspace —
   you never have to go back to the original game folder for a file that
   wasn't inside an archive. There's also an "Extract Audio Files as well?"
   checkbox, off by default, that additionally unpacks every sound
   effect/streamed track into individual .wav/.ogg files (see "Audio
   replacement" below) — it takes much longer and uses a lot more storage,
   so only check it if you actually plan on modding audio. Shows an exact
   storage-size warning (every archive entry's sector-aligned size, every
   loose file's real size, and — if audio extraction is checked — every
   sound/track's unpacked size, computed before anything is written) before
   you commit to it.

2. **Install Mod(s) into Extracted** — point this at a folder of loose
   mod-replacement files (any subfolder layout the mod author used is
   ignored, for models/textures/collision/animations) and SAFT matches
   each file by name against the manifest to figure out which archive and
   bucket folder it belongs to, then copies it into place automatically.
   Audio works here too, but only for sounds/tracks that were actually
   unpacked (i.e. you checked "Extract Audio Files as well?" when
   extracting) — a .wav/.ogg still needs its full nested folder structure
   (Package/Bank_NNN/sound_NNN.wav or Station/Track_NNN.ogg), not just a
   bare filename, same as everywhere else audio gets matched in SAFT. Only
   replacements are auto-routed this way — a file whose name doesn't match
   anything original (a brand-new addition, not a replacement) is reported
   back as unmatched for manual placement.

3. **Rebuild from Extracted** — walks the extracted tree, picks up your edits
   automatically (same filename = replaced content), drops anything you
   deleted, appends anything new, and writes fresh VER2 archives; every
   other loose file (including anything you edited straight in the
   extraction folder, like hud.txd) is carried over as-is too. Rebuild also
   checks every sound effect package and streamed station the extraction
   folder recorded as unpacked (i.e. you checked "Extract Audio Files as
   well?") and patches any sounds/tracks you edited back into a fresh copy
   of the original compressed package/station; a package/station that was
   left compressed at extraction time is simply carried over untouched like
   any other loose file. Three ways to install the result, each with a live
   storage estimate:
   - *Rebuild into a new folder* — a complete standalone second playable
     copy of the game: archives, loose files, and any reconstituted
     audio, all included.
   - *Install over the original files, with backups* — each archive AND
     each reconstituted audio package/station is backed up next to
     itself (as ".bak") before being overwritten; ordinary loose files
     are just overwritten.
   - *Install over the original files, no backups* — irreversible;
     requires an explicit confirmation.

4. **Install Mod(s) without extraction** — installs mod files straight into
   the live archives, no extraction step. A replacement that fits within
   the space its original entry already occupied is patched in place (the
   directory table never moves); one that's too big forces a rebuild of
   just that one archive, and you're asked to confirm before that happens.
   Backs up each original entry's content to a folder you choose before
   touching it, unless you explicitly opt out (with a warning).
   
5. **Uninstall Mod(s)** - Is a clone of tab 4; works the same way as 
	"Install Mod(s) without extraction" but is worded differently
	to direct the user to install their backed up vanilla files 
	in place of any unpreffered mod files. This, in essence 
	"uninstalls" any mods the user no longer wants in their 
	game directory. This option is useless for anyone who installed
	a mod without backups, which is why back ups are not only 
	reccomended but enabled by default. 

Filesystem/mod-package clutter that isn't a real game asset (.DS_Store,
Thumbs.db, desktop.ini, macOS "._*" AppleDouble sidecars, and any ".txt"
file such as a mod's readme or cleo/modloader references) are ignored everywhere files get scanned or
matched.

Every long-running step (extract, install, rebuild, copy) reports progress
per-file, not just per-archive, with a secondary progress bar/counter so
large archives (16k+ files) don't look frozen mid-operation.

### Audio replacement (sound effects AND streamed music/radio)

Both halves of San Andreas's audio are supported, and there are two ways to
replace them, since audio isn't laid out like the VER2 archives and needs its
own approach either way:

- "Install Mod(s) without extraction" matches your replacement files
  straight against the live game's audio, no extraction needed.
- "Extract Game Files" with "Extract Audio Files as well?" checked unpacks
  every sound/track into your extraction folder too. From there, "Install
  Mod(s) into Extracted" can auto-match your replacements into the
  unpacked slots for you, or you can drop them into the unpacked folders
  by hand — either way, "Rebuild from Extracted" patches your edits back in.

Either way, the file naming works the same:

- Sound effects (audio/sfx/ — gunshots, footsteps, impacts, ped lines):
  a mono 16-bit .wav laid out as `<Package>/Bank_NNN/sound_NNN.wav`
  (e.g. `GENRL/Bank_137/sound_001.wav`).
- Streamed audio (audio/streams/ — radio stations, cutscene music):
  a standard .ogg laid out as `<Station>/Track_NNN.ogg` (e.g. `AA/Track_001.ogg`).

Both naming conventions match what existing SFX/stream-editing tools
already export, and both only support same-size-or-smaller replacements —
unlike models/textures there's no rebuild fallback, since both formats pack
everything back-to-back with zero slack, so growing one would spill into
everything that comes after it in the same file. An oversized match gets
reported back clearly instead of being attempted.

---

## Building from source

Requires the .NET 8 SDK (`dotnet --version`).

```
dotnet build SAFT.sln
```

**Running the tests:**

```
dotnet test tests/SAFT.Core.Tests/SAFT.Core.Tests.csproj
```

**Publishing a portable .exe:**

```
dotnet publish src/SAFT.App/SAFT.App.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

This produces `publish/SAFT.exe` — a single file with the .NET runtime bundled in, so it runs on a
machine with no .NET installed. SAFT ships 32-bit only (`win-x86`) — it runs fine on 64-bit hardware
too and is the most broadly compatible with both Windows and Wine/Winlator.

---

## Other Complimentary GTA SA Modding tools

- [Texture Database](https://gtastuff.com/textures/)
- [Models Database](https://gtastuff.com/models/)
- [DragonDFF Model Exporter Plugin for Blender](https://github.com/Parik27/DragonFF)
- [Animation Viewer](https://gtastuff.com/ifp/)
- [Animation editor](https://gtastuff.com/tools/ifp-editor/)
- [Map Editor](https://gtastuff.com/ariane/)
- [Model Viewer and Editor](https://gtastuff.com/viewer/)
- [Collision Editor](https://gtastuff.com/col/)
- [Texture Combiner](https://gtastuff.com/tools/texture-optimizer/)
- [Sound Effects Editor](https://gtastuff.com/tools/sfx-editor/)
- [Music Editor](https://gtastuff.com/tools/streams-viewer/)
- [Texture Editor](https://www.gtagarage.com/mods/show.php?id=27862)
