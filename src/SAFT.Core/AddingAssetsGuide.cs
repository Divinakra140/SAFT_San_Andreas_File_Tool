namespace SAFT.Core;

public sealed record GuideWriteResult(bool Written, string Path, string Reason);

/// <summary>
/// Writes a plain-English guide to adding objects, ending in a checkable list of the free object
/// slots in THIS player's game.
///
/// Generated rather than bundled with the release on purpose: free slots differ from install to
/// install, so a fixed list shipped in a zip would send someone to claim a slot that's already
/// taken on their machine. It's written into the mod folder, next to the files they need to fix.
/// </summary>
public static class AddingAssetsGuide
{
    public const string FileName = "Adding_Assets_Guide.txt";

    public static GuideWriteResult TryWrite(string gameRoot, string destinationFolder, int slotsAvailable)
    {
        var writable = FolderAccess.CheckWritable(destinationFolder);
        if (!writable.CanWrite) return new GuideWriteResult(false, string.Empty, writable.Reason ?? "unknown reason");

        var path = Path.Combine(destinationFolder, FileName);
        try
        {
            File.WriteAllText(path, Build(gameRoot, slotsAvailable));
            return new GuideWriteResult(true, path, string.Empty);
        }
        catch (Exception ex)
        {
            return new GuideWriteResult(false, path, ex.Message);
        }
    }

    public static string Build(string gameRoot, int slotsAvailable)
    {
        var used = ObjectIdAllocator.ScanUsedIds(gameRoot);
        var free = FreeSlots(used).ToList();

        var text = new System.Text.StringBuilder();
        text.AppendLine("HOW TO ADD NEW OBJECTS TO GTA SAN ANDREAS WITH SAFT");
        text.AppendLine("===================================================");
        text.AppendLine();
        text.AppendLine("SAFT wrote this file for you because your mod folder had new assets in it that the");
        text.AppendLine("game has no way to load yet, or that would crash it. Everything below is measured");
        text.AppendLine("from YOUR game, so the slot numbers at the bottom are the ones actually free on this");
        text.AppendLine("install.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("THE SHORT VERSION");
        text.AppendLine("-----------------");
        text.AppendLine();
        text.AppendLine("A model file on its own does nothing. The game has to be told three things:");
        text.AppendLine();
        text.AppendLine("  1. WHAT the object is   -> a line in an .ide file");
        text.AppendLine("  2. WHERE it goes        -> a line in an .ipl file");
        text.AppendLine("  3. WHAT SHAPE it is     -> a .col collision file");
        text.AppendLine();
        text.AppendLine("Put all three in your mod folder alongside your .dff and .txd, and SAFT can install");
        text.AppendLine("the whole thing and uninstall it again later.");
        text.AppendLine();
        text.AppendLine("The third one catches people out, so it is worth saying plainly up front: if you");
        text.AppendLine("place an object and it has no collision, GTA SA CRASHES on loading a save. It does");
        text.AppendLine("not appear without being solid, and it does not misbehave quietly. It crashes, every");
        text.AppendLine("time, before you can even see the object. Step 3 below explains how to make one.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("STEP 1: WRITE THE .ide LINE (what the object is)");
        text.AppendLine("------------------------------------------------");
        text.AppendLine();
        text.AppendLine("Make a text file in your mod folder called anything you like, ending in .ide.");
        text.AppendLine("Put this in it:");
        text.AppendLine();
        text.AppendLine("    objs");
        text.AppendLine($"    {(free.FirstOrDefault() is var first and > 0 ? first : 12000)}, mycastle, mycastletxd, 300, 0");
        text.AppendLine("    end");
        text.AppendLine();
        text.AppendLine("Reading across, those five values are:");
        text.AppendLine();
        text.AppendLine("    object slot number  - pick a free one from the list at the bottom of this file");
        text.AppendLine("    model name          - your .dff WITHOUT the .dff on the end");
        text.AppendLine("    texture name        - your .txd WITHOUT the .txd on the end");
        text.AppendLine("    draw distance       - how far away it stays visible. 300 is a decent start;");
        text.AppendLine("                          small props use 100-200, big buildings 500+");
        text.AppendLine("    flags               - leave it as 0 unless you know you need something else");
        text.AppendLine();
        text.AppendLine("IMPORTANT: file names must be 23 characters or fewer, and your .dff and .txd must");
        text.AppendLine("be named EXACTLY the way you write them here.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("STEP 2: WRITE THE .ipl LINE (where it goes)");
        text.AppendLine("-------------------------------------------");
        text.AppendLine();
        text.AppendLine("Make another text file ending in .ipl, containing:");
        text.AppendLine();
        text.AppendLine("    inst");
        text.AppendLine($"    {(free.FirstOrDefault() is var f2 and > 0 ? f2 : 12000)}, mycastle, 0, 2495.5, -1690.25, 14.0, 0, 0, 0, 1, -1");
        text.AppendLine("    end");
        text.AppendLine();
        text.AppendLine("Reading across:");
        text.AppendLine();
        text.AppendLine("    object slot number  - THE SAME NUMBER you used in the .ide file");
        text.AppendLine("    model name          - the same model name again");
        text.AppendLine("    interior            - which world it belongs to. 0 is the normal outdoors;");
        text.AppendLine("                          see below. Use 0 unless you are furnishing an interior");
        text.AppendLine("    X, Y, Z             - where in San Andreas it stands");
        text.AppendLine("    four rotation values- 0, 0, 0, 1 means no rotation at all");
        text.AppendLine("    LOD                 - leave it as -1");
        text.AppendLine();
        text.AppendLine("The coordinates above are Grove Street, outside CJ's house, at ground level. A map");
        text.AppendLine("editor will give you coordinates for anywhere else.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("ABOUT THE INTERIOR NUMBER");
        text.AppendLine();
        text.AppendLine("San Andreas keeps its building interiors in the same map as the outside world, just");
        text.AppendLine("parked far away where you can never walk to them. The interior number is how the");
        text.AppendLine("game knows which of those worlds an object belongs to. You only ever see objects");
        text.AppendLine("whose number matches the one you are currently in, so an object in the wrong");
        text.AppendLine("interior is invisible rather than broken.");
        text.AppendLine();
        text.AppendLine("Counted from this install's own map files, Rockstar uses:");
        text.AppendLine();
        text.AppendLine("    0             the ordinary outdoor world - 7,886 of their placements");
        text.AppendLine("    1 to 18       the individual interiors: safehouses, shops, clubs, mission");
        text.AppendLine("                  interiors. A few hundred placements between them");
        text.AppendLine("    256 and up    a handful of theirs carry extra flags in the high bits. Not");
        text.AppendLine("                  something a mod needs to touch");
        text.AppendLine();
        text.AppendLine("Practically: use 0. If you are placing furniture inside an existing interior, use");
        text.AppendLine("that interior's number, which a map editor shows you when you open it. There is no");
        text.AppendLine("list of which number is which building in the game files, so the editor is the");
        text.AppendLine("reliable way to find it.");
        text.AppendLine();
        text.AppendLine("You can repeat that line as many times as you like with different coordinates to");
        text.AppendLine("place the same object in several places. That still only uses ONE slot.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("STEP 3: MAKE THE .col FILE (what shape it is)  ** REQUIRED **");
        text.AppendLine("------------------------------------------------------------");
        text.AppendLine();
        text.AppendLine("WHY THIS IS NOT OPTIONAL");
        text.AppendLine();
        text.AppendLine("A collision file describes the invisible solid shape of your object: the thing CJ");
        text.AppendLine("walks into and cars crash against. You might reasonably think that leaving it out");
        text.AppendLine("just means people walk through your castle. It does not.");
        text.AppendLine();
        text.AppendLine("While the game builds the world, it asks every placed object how big it is, and it");
        text.AppendLine("gets that answer from the collision file. If there isn't one, the game crashes on the");
        text.AppendLine("spot. That happens when you load a save or start a new game, BEFORE you can see");
        text.AppendLine("anything, and it happens no matter where on the map you put the object. The other");
        text.AppendLine("side of San Andreas is just as fatal as standing right next to it.");
        text.AppendLine();
        text.AppendLine("This was tested rather than guessed at. Starting from a working install with the");
        text.AppendLine("object visible in the game, its collision record was deleted and nothing else was");
        text.AppendLine("touched: same model, same texture, same slot, same position. The game crashed on");
        text.AppendLine("loading the save. Putting the record back fixed it.");
        text.AppendLine();
        text.AppendLine("So SAFT refuses to install a placed object with no collision. It is not being fussy:");
        text.AppendLine("installing it would hand you a game that will not load.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("THE ONE RULE THAT MATTERS");
        text.AppendLine();
        text.AppendLine("The game finds collision by the model name stored INSIDE the .col file. It does not");
        text.AppendLine("care what the .col file itself is called.");
        text.AppendLine();
        text.AppendLine("    your model file  ->  mycastle.dff");
        text.AppendLine("    the name inside  ->  mycastle          <- this is what must match");
        text.AppendLine("    the file's name  ->  anything.col      <- this can be whatever you like");
        text.AppendLine();
        text.AppendLine("One .col file can hold collision for as many models as you want, so most mods ship a");
        text.AppendLine("single .col for the whole pack. That is fine. SAFT looks inside the file, not at its");
        text.AppendLine("name.");
        text.AppendLine();
        text.AppendLine("If the name inside doesn't match, the game behaves exactly as if there were no");
        text.AppendLine("collision at all, and it crashes. A typo here is the most common cause.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("MAKING SOMETHING YOU CAN WALK THROUGH");
        text.AppendLine();
        text.AppendLine("What matters is that a record EXISTS for your model. What is inside it is up to you.");
        text.AppendLine();
        text.AppendLine("A collision file with no geometry in it (no spheres, no boxes, no faces, just the name");
        text.AppendLine("and the bounding box) loads perfectly happily, and you walk straight through the object.");
        text.AppendLine("That is how you make pass-through scenery on purpose: smoke, light beams, hanging");
        text.AppendLine("vines, decorative clutter you don't want to trip over. San Andreas does this itself;");
        text.AppendLine("the police stinger is stored exactly that way.");
        text.AppendLine();
        text.AppendLine("So the rule is simply:");
        text.AppendLine();
        text.AppendLine("    no record at all   ->  the game CRASHES on loading a save");
        text.AppendLine("    an empty record    ->  fine, you walk through it");
        text.AppendLine("    a record with shapes -> fine, it is solid");
        text.AppendLine();
        text.AppendLine("Both of those last two were tested in game. SAFT tells you after installing if any of");
        text.AppendLine("your objects came out walk-through, in case you emptied a record by accident.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("HOW TO MAKE ONE");
        text.AppendLine();
        text.AppendLine("You need a collision editor. The one most SA modders use is CollEditor2 (sometimes");
        text.AppendLine("written 'Collision Editor II'), which is free and widely mirrored on the GTA modding");
        text.AppendLine("sites. Steve M's CollEditor is the older alternative. 3ds Max users can also export");
        text.AppendLine("collision directly with Kam's GTA scripts.");
        text.AppendLine();
        text.AppendLine("The usual workflow in a collision editor:");
        text.AppendLine();
        text.AppendLine("  1. Open a NEW collision file, or open an existing .col to work from.");
        text.AppendLine("  2. Add a collision model and set its name to your .dff name, without the .dff.");
        text.AppendLine("  3. Import the shape. You can import your .dff directly and let the editor build");
        text.AppendLine("     collision from the model's own geometry.");
        text.AppendLine("  4. Save as COL3 (also shown as 'COL 3' or 'San Andreas'), which is the format SA");
        text.AppendLine("     uses. COL2 also works. Save the file into your mod folder.");
        text.AppendLine();
        text.AppendLine("A word of advice on shape: collision does NOT have to match your model exactly, and");
        text.AppendLine("it should not. Collision made from a very detailed model is slow and can behave");
        text.AppendLine("badly. A simple box or a rough version of the shape is what the game's own buildings");
        text.AppendLine("use, and it is what you want. Simple collision on a detailed model is completely");
        text.AppendLine("normal.");
        text.AppendLine();
        text.AppendLine("If your object came from someone else's mod and has no .col, ask the author for it.");
        text.AppendLine("That is a normal request; a map mod without collision is an unfinished mod.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("EDITING COLLISION THAT ALREADY EXISTS");
        text.AppendLine();
        text.AppendLine("Open the .col in the collision editor, select the model inside it, and you can move");
        text.AppendLine("or resize the collision boxes, or re-import a different shape. Save it back out as");
        text.AppendLine("COL3 into your mod folder.");
        text.AppendLine();
        text.AppendLine("SAFT merges your collision into the game and, when you uninstall, takes exactly your");
        text.AppendLine("records back out again and leaves everyone else's alone.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("THE ONE CASE WHERE YOU CAN SKIP IT");
        text.AppendLine();
        text.AppendLine("If you only want to ADD a model without PLACING it anywhere (a car, a weapon, or");
        text.AppendLine("something a script spawns later) you do not need collision from SAFT's point of");
        text.AppendLine("view, because nothing is being placed into the world. That means an .ide with no");
        text.AppendLine(".ipl. Vehicles and weapons carry their own collision inside the model anyway.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("A NOTE ON SIZE");
        text.AppendLine("--------------");
        text.AppendLine();
        text.AppendLine("GTA SA has a limited amount of memory for loading things. Very detailed models and");
        text.AppendLine("high resolution textures use it up fast, and when it runs out the game stops drawing");
        text.AppendLine("other parts of the world. SAFT works best with LQ (Low Quality) and SA-style assets");
        text.AppendLine("that are not more detailed than the original game's. SAFT will tell you before");
        text.AppendLine("installing if what you're adding looks too heavy.");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("=========================================================================");
        text.AppendLine($"FREE OBJECT SLOTS IN YOUR GAME  ({slotsAvailable} available)");
        text.AppendLine("=========================================================================");
        text.AppendLine();
        text.AppendLine("Please feel free to edit this .txt and save your edits every time you use an empty");
        text.AppendLine("object slot, deleting the [X] at the end of the slot, to help you keep track of");
        text.AppendLine("what slots you still have available.");
        text.AppendLine();
        text.AppendLine("These were read from your own game folder. Another install will have a different");
        text.AppendLine("list, so don't copy these numbers to a different computer.");
        text.AppendLine();

        foreach (var id in free) text.AppendLine($"    {id}  [X]");

        return text.ToString();
    }

    /// <summary>
    /// Every ID free for a map object: the gaps between what the game already defines, plus the
    /// headroom above its highest. IDs below the map-object range belong to peds, weapons and
    /// vehicles and are deliberately excluded.
    /// </summary>
    private static IEnumerable<int> FreeSlots(IReadOnlySet<int> used)
    {
        for (var id = ObjectIdAllocator.LowestMapObjectId; id < ObjectIdAllocator.DefaultEngineModelLimit; id++)
            if (!used.Contains(id)) yield return id;
    }
}
