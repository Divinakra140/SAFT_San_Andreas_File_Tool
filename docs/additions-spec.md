# SAFT 2.0 — Asset Additions (design spec)

Working document. Captures what was decided and, more importantly, what was *verified* against a
real game install, so none of it has to be re-derived later.

Status: designed, not built. SAFT 1.6 is replacement-only and complete.

---

## The idea in one line

SAFT can already *add* files (rebuild appends anything not in the manifest). What it can't do is
**register** them so the game loads them, or **remember** them so they can be removed. 2.0 closes
both gaps.

---

## Verified facts

Everything here was measured against a stock v3.0 ("newsteam r2") install, not assumed.

**Adding already half-works.** `Rebuilder.cs`: *"files present on disk but absent from the manifest
are appended at the end."* `CopyLooseFiles` carries edited `.ide`/`.ipl` through a rebuild. So today
a user can already add assets by hand-editing two text files and rebuilding — 2.0 automates that and
makes it reversible.

**Object ID space.** 14,832 IDs defined across 59 `.ide` files, spanning 0–18630.

| | |
|---|---|
| Free gaps inside the used range (616–18630) | 3,758 |
| Headroom above 18,630 (to the ~20,000 engine cap) | 1,369 |
| **Total usable for new map objects** | **~5,127** |

Largest contiguous gaps: `11682–12799` (1,118), `15065–15999` (935), `13891–14382` (492). IDs below
616 are ped/weapon/vehicle space and must not be used for map objects.

Caveat: the 20,000 cap is the commonly cited engine limit, taken from general modding knowledge, not
measured. The 3,758 figure is solid regardless — it derives only from IDs the game actually defines.

**Collision bundles map 1:1 to `.ide` files.** This was the one real unknown, and it resolved
cleanly:

```
las_2.col        82 models  ->  all 82 defined in LAs.ide
sfs_7.col        40 models  ->  all 40 defined in SFs.ide
countn2_10.col   50 models  ->  all 50 defined in countn2.ide
```

So **there is no coordinate lookup to build.** A new object's collision belongs in a bundle
associated with the same `.ide` file the object is defined in — a decision already made by the time
collision matters.

Inferred but not verified: *why*. `gta.dat` lists `.ide`/`.ipl` but never `.col`, and `.ide`/`.ipl`
only ever reference model *names* — so the engine most likely scans the archive for `.col` entries
and indexes collision by name, making the region naming a convention rather than a requirement.
Doesn't change the plan either way: following Rockstar's own convention is safe under both readings.

**`.col` bundles are appendable.** They're a run of self-describing records — FourCC (`COL3`),
4-byte size, 22-byte name, data — concatenated with no directory table. Appending is writing a
record at the end. Simpler than the IMG format SAFT already handles. If a bundle outgrows its sector
allocation, that's the existing rebuild path.

**File format reminders.** `.ide` defines (`17515, scumgym1_LAe, ganton01_lae2, 100, 128` — ID,
model, texture, draw distance, flags; no coordinates). `.ipl` places (`17512, LODgwforum1_LAe, 0,
2737.75, -1760.06, 26.23, 0,0,-8.7e-008,1, -1` — ID, model, interior, X, Y, Z, rotation quaternion,
LOD). Both have sections; additions go in `objs` and `inst` respectively. IMG entry names are capped
at 23 characters (already validated by `ImgArchive`).

---

## Architecture

### Where placement comes from

**The mod author supplies it.** An addition mod ships assets plus `.ide`/`.ipl` snippets — the author
already decided where their castle goes. SAFT's job is to *merge* those snippets and *reallocate the
ID* so it doesn't collide on this particular install.

This is why no new tab and no coordinate-entry UI is needed: SAFT stays in the role it already
plays — the author decides what and where, SAFT works out how to get it in cleanly.

### ID allocation is self-correcting

After mod A claims 12000, the game's `.ide` files *contain* 12000, so mod B's scan sees it taken.
The game files are the source of truth; SAFT never tracks allocations across installs. The manifest
exists for uninstall attribution only.

Allocation must rewrite the ID **consistently across both snippets** — a mod shipping `12000` in its
`.ide` and `.ipl` gets both rewritten to the same free slot.

### The additions manifest

**Lives in the backup folder**, written at install time alongside the backed-up replacement
originals. This is the hinge of the whole design: an added asset has no vanilla counterpart, so
nothing about it lands in the backup folder naturally. Without the manifest living there, tab 5 has
no way to know an addition ever happened.

Records per addition:
- archive each new entry went into, and its entry name
- exact `.ide` lines added, and to which file
- exact `.ipl` lines added, and to which file
- allocated IDs (so they return to the pool)
- `.col` records added, and to which bundle
- a hash or size of each thing added

That last field matters: uninstall must distinguish "untouched, safe to remove" from "the user
edited this since." Same lesson as the `arrow.dff` dual-location case — verify the world still
matches your assumption before acting on it.

### Uninstall

Tab 5 checks for an additions manifest in the chosen backup folder.

- **None found** → proceed with the existing 1.6 restore, unchanged.
- **Found** → run additions-removal first, then the 1.6 restore for replacements.

Removal is **surgical, not a revert**: remove only this mod's `.ide`/`.ipl` lines, matched **by
content, not line number** (files shift as other mods come and go), preserving other additions. A
line that no longer matches is skipped and reported rather than force-removed. Reverting to a
vanilla data sheet would silently delete other mods' additions and is never correct.

Removing an appended archive entry requires a **full archive rebuild** — minutes, where a
replacement-uninstall is seconds.

---

## Popups

No new tab. Three additions to existing flows.

### 1. New assets detected (the main one)

> Your mod folder contains new assets that are not in your game directory. These would take up
> **N** of your game's asset slots. SAFT can add them because there are currently **M** available
> and compatible slots. Would you like SAFT to add them, and write backup-logs of how they were
> added, so you can cleanly uninstall them later? This would leave you with **M − N** empty slots.
>
> - **Yes, add new assets and write backup-logs**
> - **No, ignore any new files, only replace preexisting files**

There is deliberately **no option to add without logging.** Nobody should end up with additions they
can't cleanly uninstall.

"No" reverts to 1.6 behaviour exactly. "Yes" proceeds, then shows:

> Adding new assets… Please note that any assets added through other tools or methods will not be
> uninstallable through SAFT. But if SAFT added it in, SAFT can remove it later.
>
> - **OK, understood**

**Slot arithmetic:** count `.ide` *definitions*, not files. A `.txd` consumes zero slots — textures
are referenced by name from the `.ide` and never get object IDs. A 100-file mod might need 20 slots
or 3.

### 2. New assets with no placement data

> Your mod folder contains new assets that are not in your game directory, but it doesn't include
> the `.ide` and `.ipl` files that tell the game what these assets are and where they appear
> (`.ide` = what the object is, `.ipl` = where it goes). Without them, SAFT can copy the files in
> but the game will never show them — they would take up asset slots and appear nowhere.
>
> - **Yes, install the other files in my mod folder that are formatted correctly**
> - **No, stop this installation so I can try again with the supporting files in place**

### 3. Uninstall requires a rebuild

> Uninstalling this will also require rebuilding the archives, because your mods added assets on
> top of the originals. This adds time to the uninstall. Continue?
>
> - **Yes, uninstall and rebuild**
> - **No, don't start the uninstall yet**

### Not a popup: missing collision

An added object with no `.col` still appears and works — it just isn't solid, which is frequently
intentional (decorative props, LOD models, 2D effects; LODs never have collision). Blocking on it
would cry wolf on legitimate mods. Report it in the completion summary instead:

> N added object(s) have no collision and will not be solid.

---

## Wording conventions

Use `.ide` and `.ipl` with the dot, not bare "IDE"/"IPL" — bare "IDE" reads as *integrated
development environment*, and the dotted form sends users looking for an actual file. Matches the
existing `data > script > main.scm` style.

---

## Also changes

- Side panel art: "Replaces:" becomes "Adds / Replaces:"
- README: same change, plus documenting the additions workflow

---

## Open questions

- **Verification is the real risk.** Every 1.6 feature was checkable from files. "Does the castle
  appear at the right spot, at the right size, and is it solid?" is only answerable by launching the
  game and walking there. Expect more round trips than the Winlator work took, and more ways to be
  subtly wrong (draw distance, LOD, wrong collision bundle, invisible past 100m).
- Confirm the 20,000 engine cap against a limit adjuster's documentation.
- Confirm the "engine indexes collision by name" inference.
- Approaching an engine limit can only be *detected and reported*, never fixed — raising it needs a
  limit adjuster, which is a compiled `.asi`, version-specific, and squarely in the category SAFT
  refuses. Detect and warn; never bundle.

---

## Out of scope

CLEO support. It's additive plumbing requiring an `.asi` runtime, it adds files to the game folder
against SAFT's stated promise, it degrades the emulator compatibility SAFT exists to protect, and
CLEO scripts can touch save state — so the uninstall guarantee would be weaker than SAFT's standard.
SAFT already handles the useful half: the *assets* a CLEO mod ships, when they're named like vanilla
files.
