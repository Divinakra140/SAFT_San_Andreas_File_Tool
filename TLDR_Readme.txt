

SAFT - San Andreas File Tool
=============================

-Developed by Divinakra, August 2026

TLDR Readme only contains the user guide and is a shorter and more lazy version,
if you run into issues, read the full long readme before contacting me.

SAFT comes as two exes. SAFT.exe has two tabs - "Install Mods" and "Uninstall Mods" - and everything
in it takes seconds, so it is the one to use on Android emulators. SAFT-Dev.exe has those two plus
Extract, Install into Extracted and Rebuild, for mod developers working on Windows.

SAFT is like the mechanic who understands your car very well, and can quickly and cleanly install any
new engine parts you want to bring to him as long as they fall into the 11 categories:

1.Models (.dff)
2.Collision (.col)
3.Textures (.txd)
4.Animations (.ifp)
5.Audio (.wav for SFX, .ogg for music)
6.Map Data (.ipl, .ide)
7.Paths (nodes .dat, .rrr car recordings)
8.Data Tables (.dat, .cfg, .zon, .ped, .grp)
9.Text (.gxt)
10.Cutscenes (.cut, .mpg)
11.Particle Effects (.fxp)

And as long as they are named correctly ... meaning the same as the vanilla files they aim to replace.
SAFT also ADDS files the game has never had - anything not matching a game file is treated as an
addition, and SAFT asks you first in case you just misnamed a replacement.
---------------------------------------------------------------------------------------------------------
User Guide:
----------

1. Open up the SAFT folder and double click SAFT.exe.

2. Decide what you want to do, do you want to quickly replace some files? Or do you want to extract 
your whole game and manually sort through files replacing them manually and then rebuilding the game
from that extracted folder? 

3. For quick replacement of files. Simply find the mod(s) you want online and unzip the .zip archive 
to see the full folder structure of the mod. That main mod folder is what you want to provide to SAFT.

4. Then click "Install Mods" (in SAFT-Dev it is called "Install Mod(s) without extraction")

5. Then browse for your "Game Folder" where the gta-sa.exe is located, and select that folder that 
contains your gta-sa.exe as the "Game Folder". (it doesn't matter what your game .exe is named,
gta_sa.exe or gta-sa.exe or ahksgwhdiuh.exe will all work both to mod and launch the game)

6. Then for "Mod Folder", browse to the mod folder you downloaded and unzipped in step 3.

7. Then pick a "Backup folder". Your vanilla files are always backed up there before anything is
replaced - this is what makes uninstalling possible later, so it is not optional.

8. Then hit "Install Mod into Game files" and you should see some loading screens. Thats it!

9. Answer the windows SAFT shows you. It will say how heavy the mod is (green/amber/red, below), it
will ask before rebuilding an archive to fit bigger files, and if your mod contains files the game
does not already have it will ask whether you meant to ADD them or misnamed a replacement. Nothing is
written until you have answered. Then you're done - launch the game normally.

10. To uninstall, use the "Uninstall Mods" tab and point it at the backup folder you used when you
installed. Whatever vanilla files it finds there go back where they belong, and anything SAFT ADDED
is removed too - its object slots freed, its map entries deleted. Use a separate backup folder per
mod if you want to remove them individually, or one folder for everything to undo the lot at once.

11. To extract the whole game, edit files by hand and rebuild it, use SAFT-Dev's first three tabs in
order: extract, install, rebuild. Same end result as the fast route, but you see every filename and
how it is laid out. Mod devs modifying audio should tick "extract audio files as well" - off by
default because it takes far longer. Be warned: extraction writes over 20,000 separate files and can
take a very long time under Android emulation, so use Windows for it if you can. Casual users never
need this. Whichever route you take, audio must sit in its nested folder structure, not as loose
.wav/.ogg files - audio is the only type SAFT identifies by folder rather than by name.

-----------------------------------------------------------------------------------------
Green, Amber and Red: will this mod still render?
-------------------------------------------------

San Andreas streams the world in as you move, and every area has a budget. A mod that makes one area
much heavier than the game expects shows up as objects failing to appear until you stop and look at
them. SAFT weighs your mod before installing anything and tells you where you land:

  GREEN   up to 2.2x vanilla   renders perfectly everywhere
  AMBER   2.3x to 3.1x         breaks where the mod loads heaviest, rest of the game is fine
  RED     above 3.2x           breaks everywhere you go; around 4x it can also crash

Those come from playing the game at each weight on a Retroid Pocket Flip 2, not from a formula. The
figure SAFT shows is what your game holds AFTER installing, so a mod going onto an already-heavy game
is judged on the result rather than on how big a step it is. Every mod puts its weight somewhere
different, so treat these as the shape of the problem rather than exact lines. SAFT is at its best
with SA-style and LQ mods; 4K retextures of everything are what red is for.

-----------------------------------------------------------------------------------------
Acceptable File Types and Names:
--------------------------------

All mod files must be named identically to the games files for SAFT to replace them. 

SAFT only accepts the native GTA SA file types, no .DAE for models, no mp3 for audio... ect..

1. For Models: .dff (use DragonDFF plugin for blender)
2. For Collision: .col
3. For Textures: .txd
4. For Animations: .ifp
5. For Audio: mono 16-bit .wav for SFX and .ogg for Music (both are built in export options from audacity)
6. For Map Data: .ipl (where every object sits in the world) and .ide (what each object IS - its
model, its texture, its draw distance). This is what map mods are made of.
7. For Paths: nodes*.dat - the invisible road and pavement network vehicles and pedestrians navigate
on - plus .rrr car recordings, the pre-recorded routes scripted vehicles follow (mission cars, the
train, planes on set flight paths). Replacing one changes that vehicle's route and speed.
8. For Data Tables: handling.cfg (vehicle handling), weapon.dat, carcols.dat, carmods.dat, .zon map
zones, .ped and .grp pedestrian behaviour, and the rest of the tables in the data folder.
9. For Text: .gxt - every subtitle, mission caption and menu string in the game, which means full
translations can be installed with SAFT.
10. For Cutscenes: .cut cutscene data, and the .mpg intro movies.
11. For Particle Effects: .fxp - explosions, fire, smoke, water spray, muzzle flashes and the rest of
the game's particle system.

That covers essentially every asset in the game. To put numbers on it, a stock v3 install holds
15,334 models, 3,983 textures, 435 animations, 426 car recordings, 251 collision files, 190 map
placement files and 212 data tables inside the archives, plus another 398 files sitting loose in
the game folder. SAFT can replace all of them.
