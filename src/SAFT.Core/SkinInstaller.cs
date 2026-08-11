namespace SAFT.Core;

/// <summary>The pair of files a user supplies for a skin: the model and its textures.</summary>
public sealed record SkinSource(string ModelPath, string TexturePath);

/// <summary>
/// Something SAFT found wrong with a skin before installing it. <see cref="Blocking"/> separates
/// "this file is not what it claims to be" — where installing would put a malformed entry into the
/// archive and the game would hang on the loading screen — from "this will load, and here is how it
/// will look wrong", which is the user's call to make.
/// </summary>
public sealed record SkinIssue(string Message, bool Blocking);

/// <summary>
/// What SAFT can tell about a skin from the files alone, before anything is written.
///
/// Every check here was run against all 265 vanilla ped model/texture pairs in this game and none of
/// them tripped it, so an issue reported is a real difference between the user's files and how the
/// game's own assets are built — not the checker being fussy.
/// </summary>
public sealed record SkinInspection(
    RwModelInfo? Model,
    RwTextureDictionaryInfo? Textures,
    IReadOnlyList<SkinIssue> Issues)
{
    public bool CanInstall => !Issues.Any(i => i.Blocking);

    public IReadOnlyList<SkinIssue> Blocking => Issues.Where(i => i.Blocking).ToList();
    public IReadOnlyList<SkinIssue> Warnings => Issues.Where(i => !i.Blocking).ToList();
}

/// <summary>
/// Installs a custom character model into an existing pedestrian slot, by replacing what that slot's
/// model and texture files contain.
///
/// WHAT THIS DOES AND DOES NOT DO. It changes the appearance of a pedestrian slot. It does not make
/// the player be that pedestrian — nothing in the game's data files decides that, so a skin selector
/// is still what points the player at the slot. What this adds is that the slot is now permanently
/// the user's model, at the file level, surviving every restart with no script running.
///
/// WHY IT WRITES TO MORE THAN ONE NAME. 32 of this game's peds ship a second, higher-detail model the
/// engine swaps in at close range — wmyst.dff has swmyst.dff beside it. Replacing only the first
/// gives a character who visibly turns back into the vanilla pedestrian when the camera comes close.
/// <see cref="PedSlot.ModelTargets"/> lists every name that has to receive the file.
///
/// WHY IT IS SAFE FOR SAVES. Nothing here touches data/peds.ide, main.scm or script.img. No model ID
/// is added, removed or renumbered, so a save — which stores ped references by ID — still means
/// exactly what it meant before. Undoing the swap is restoring a handful of archive entries, which is
/// what SAFT's existing backup and uninstall path already does.
/// </summary>
public static class SkinInstaller
{
    /// <summary>
    /// Reads the user's two files and reports what is wrong with them, without touching the game.
    ///
    /// The two failure modes worth naming: an unskinned model loads and then stands in a T-pose,
    /// because there are no bone weights for the animation system to drive; and a model asking for
    /// texture names the dictionary does not supply renders untextured white. Neither shows up as an
    /// error at install time — the files are perfectly well-formed — so the only place to catch them
    /// is here.
    /// </summary>
    public static SkinInspection Inspect(SkinSource source)
    {
        var issues = new List<SkinIssue>();

        var model = ReadOrNull(source.ModelPath, RenderWare.ReadModel, issues, "model", ".dff");
        var textures = ReadOrNull(source.TexturePath, RenderWare.ReadTextureDictionary, issues, "texture dictionary", ".txd");

        if (model is not null)
        {
            if (!model.IsSkinned)
                issues.Add(new SkinIssue(
                    "The model has no skinning data, so the animation system has no bones to drive. " +
                    "It will load and then stand still in a T-pose.", Blocking: false));

            var size = new FileInfo(source.ModelPath).Length;
            if (size < RenderWare.PlausibleModelMinBytes)
                issues.Add(new SkinIssue(
                    $"The model is {size:N0} bytes. Every ordinary character in this game is between " +
                    $"{RenderWare.PlausibleModelMinBytes / 1024} KB and 400 KB, so this is very likely " +
                    "a skeleton or a prop rather than a body.", Blocking: false));
            else if (size > RenderWare.PlausibleModelMaxBytes)
                issues.Add(new SkinIssue(
                    $"The model is {size / (1024 * 1024.0):N1} MB, far larger than any character the " +
                    "game ships. It will work, but it counts against the streaming budget of wherever " +
                    "it appears.", Blocking: false));
        }

        if (textures is not null && !textures.CountAgrees)
            issues.Add(new SkinIssue(
                $"The texture dictionary says it holds {textures.DeclaredCount} textures but " +
                $"{textures.Textures.Count} could be read. The file is damaged.", Blocking: true));

        if (model is not null && textures is not null)
        {
            var supplied = textures.Textures.Select(t => t.Name);
            var missing = model.TextureNames.Except(supplied, StringComparer.OrdinalIgnoreCase).ToList();

            if (missing.Count > 0)
                issues.Add(new SkinIssue(
                    $"The model asks for {Join(missing)}, which the texture dictionary does not " +
                    "contain. Those parts of it will render plain white.", Blocking: false));

            if (model.TextureNames.Count == 0 && textures.Textures.Count > 0)
                issues.Add(new SkinIssue(
                    "The model names no textures at all, so nothing in the texture dictionary will be " +
                    "applied to it.", Blocking: false));
        }

        return new SkinInspection(model, textures, issues);
    }

    private static T? ReadOrNull<T>(
        string path, Func<string, T?> read, List<SkinIssue> issues, string what, string extension)
        where T : class
    {
        if (!File.Exists(path))
        {
            issues.Add(new SkinIssue($"No {what} file at '{path}'.", Blocking: true));
            return null;
        }

        T? result;
        try
        {
            result = read(path);
        }
        catch (Exception ex)
        {
            issues.Add(new SkinIssue($"The {what} '{Path.GetFileName(path)}' could not be read: {ex.Message}", Blocking: true));
            return null;
        }

        if (result is null)
            issues.Add(new SkinIssue(
                $"'{Path.GetFileName(path)}' is not a {extension} — it does not begin like one. " +
                "Renaming a file does not convert it, and installing it would leave the game hanging " +
                "on its loading screen.", Blocking: true));

        return result;
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? $"texture '{names[0]}'" : "textures " + string.Join(", ", names.Select(n => $"'{n}'"));

    /// <summary>
    /// Works out every archive write the swap needs, without performing any of them. The result is an
    /// ordinary <see cref="DirectInstallPlan"/>, so it goes through exactly the same install path as
    /// a normal mod — the same backups, the same in-place patch, the same rebuild when a replacement
    /// is too big to fit, the same flush to disk at the end.
    /// </summary>
    public static DirectInstallPlan Plan(string gameRoot, PedSlot slot, SkinSource source)
    {
        if (!slot.CanHostASkin)
            throw new InvalidOperationException(
                $"Model ID {slot.ModelId} ({slot.ModelName}) cannot host a skin: " +
                (slot.IsPlayerSlot ? "it is CJ, who is assembled from parts in player.img rather than loaded from one model."
                 : slot.IsSpecialCharacterSlot ? "it is a special-character placeholder, which holds no model of its own."
                 : "its model or texture is not in the archive."));

        var archivePath = PedSlotCatalog.PedArchivePath(gameRoot);
        var relativePath = Path.GetRelativePath(gameRoot, archivePath);

        using var archive = ImgArchive.Open(archivePath);
        var entries = archive.Entries.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);

        var matches = new List<DirectInstallMatch>();
        Add(slot.ModelTargets, source.ModelPath);
        Add(slot.TextureTargets, source.TexturePath);

        void Add(IReadOnlyList<string> targets, string sourcePath)
        {
            var length = new FileInfo(sourcePath).Length;
            var neededSectors = (length + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize;

            foreach (var target in targets)
            {
                // Built from the catalog, which only names entries it saw in this archive — but the
                // archive is reopened here, so a slot list from an earlier scan of a since-changed
                // game would otherwise write nowhere and report success.
                if (!entries.TryGetValue(target, out var entry))
                    throw new InvalidOperationException(
                        $"'{target}' is no longer in {relativePath}. Re-scan the game folder before installing.");

                matches.Add(new DirectInstallMatch(
                    FileName: Path.GetFileName(sourcePath),
                    ModFilePath: sourcePath,
                    ArchiveRelativePath: relativePath,
                    ArchiveAbsolutePath: archivePath,
                    EntryName: entry.Name,
                    RequiresRebuild: neededSectors > entry.SizeSectors));
            }
        }

        return new DirectInstallPlan(
            GameRoot: gameRoot,
            Matches: matches,
            Unmatched: Array.Empty<string>(),
            TotalArchivesInGame: 1,
            AudioMatches: Array.Empty<DirectAudioMatch>(),
            AudioUnmatched: Array.Empty<string>(),
            StreamMatches: Array.Empty<DirectStreamMatch>(),
            StreamUnmatched: Array.Empty<string>(),
            UnarchivedMatches: Array.Empty<DirectUnarchivedMatch>(),
            Ambiguous: Array.Empty<DirectAmbiguousMatch>(),
            RefusedScripts: Array.Empty<RefusedScript>());
    }

    /// <summary>
    /// Installs the skin. <paramref name="backupOutputFolder"/> is required rather than optional: a
    /// swap with no backup cannot be undone, and the uninstall path is the whole reason this is safe
    /// to offer.
    /// </summary>
    public static DirectInstallResult Apply(
        DirectInstallPlan plan, string backupOutputFolder, IProgress<DirectInstallProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupOutputFolder);
        return DirectModInstaller.Apply(plan, backupOutputFolder, progress);
    }
}
