namespace SAFT.Core;

/// <summary>
/// The finished verdict shown to the user: the measured numbers, what they mean in plain language,
/// and whether the app should ask permission before continuing.
/// </summary>
/// <summary>How alarming a verdict is, so the dialog can signal it at a glance before any reading.</summary>
public enum StreamingSeverity
{
    /// <summary>Within what the player's own game already handles. Green.</summary>
    Fine,

    /// <summary>Past that, but likely only noticeable in the densest areas. Yellow.</summary>
    Caution,

    /// <summary>Heavy enough to expect problems throughout the game. Red.</summary>
    Serious,
}

public sealed record StreamingVerdict(bool WithinRange, string Message, StreamingSeverity Severity)
{
    /// <summary>
    /// Within range needs only an acknowledgement; outside range is a decision, so the caller offers
    /// continue/cancel rather than presenting a fait accompli.
    /// </summary>
    public bool NeedsConfirmation => !WithinRange;
}

/// <summary>
/// Turns the density and streaming measurements into something a person can act on.
///
/// Everything quoted here is measured from the player's own game — the mod's object count, its
/// weight, and the busiest/heaviest area Rockstar themselves shipped. The one judgement call is the
/// dynamic-weight multiplier below, which is labelled as such rather than dressed up as a
/// measurement.
/// </summary>
public static class StreamingAdvice
{
    /// <summary>
    /// How much heavier a mod may make the SPAWNED side of the load - vehicles, peds, weapons -
    /// before SAFT asks permission instead of just informing.
    ///
    /// Applies only to that side. The placed-map side used to be judged by this same number and is
    /// now judged on its absolute weight instead, because that side has since been measured directly
    /// and the measurements disagreed with it: see <see cref="PlayedFineMultipleOfStock"/>. Nobody
    /// has run the equivalent experiment on spawned models, so this stays a judgement here.
    ///
    /// This is a judgement, not a measurement, and it is deliberately conservative. The reasoning:
    ///
    ///   ~1.0x  SA-style / LQ car packs — reported as flawless everywhere.
    ///    2.01x NeXtGen Remaster Weapons — measured against a real install; broke world rendering,
    ///          but only downtown, which is the heaviest district in the game.
    ///
    /// The engine has ONE streaming budget that placed scenery and spawned traffic both draw from,
    /// so the same mod can be fine in the countryside and break in a dense district. The true
    /// failure line therefore sits somewhere below 2.01x and we have no sample between. Setting the
    /// limit AT the known-bad point would pass mods at 1.9x that plausibly fail in the same place.
    ///
    /// Since SAFT's promise is "this will perform well" — not "this will perform well unless you go
    /// downtown" — the limit sits well clear of the one failure we've measured. Revisit only with
    /// real evidence of a mod above this that behaves everywhere, downtown included.
    /// </summary>
    public const double DynamicWeightMultiplierLimit = 1.5;

    /// <summary>
    /// Above this, trouble is expected throughout the game rather than only in dense districts.
    ///
    /// Bounded by two measurements rather than picked freely: NeXtGen Remaster Weapons at 2.01x
    /// broke rendering ONLY downtown, while a 9.5x HD car pack broke it everywhere, all the time.
    /// The boundary between "dense areas only" and "everywhere" therefore lies between those, and
    /// 3.0 is the conservative choice within that gap.
    /// </summary>
    public const double SevereWeightMultiplier = 3.0;

    /// <summary>
    /// How heavy the busiest 200m area of a STOCK San Andreas is.
    ///
    /// Measured from a clean install rather than assumed: 32.0 MB, and stable — the second heaviest
    /// area came to 29.9 MB, a ratio of 1.07, so this is the top of a smooth distribution rather
    /// than one freak cell. Different releases of the game should land near it, since it is the same
    /// map data, but that hasn't been verified across releases, which is why the wording says
    /// "about" and "a stock copy" rather than quoting it as law.
    /// </summary>
    public const long StockHeaviestAreaBytes = 33_562_624;

    /// <summary>
    /// The heaviest area a game can reach, as a multiple of stock, and still have been PLAYED and
    /// found normal everywhere.
    ///
    /// Measured, not reasoned about. Three packs have been driven through their own district and the
    /// rest of the map with no rendering difference of any kind, at 1.80x, 1.90x and 2.19x stock.
    /// </summary>
    public const double PlayedFineMultipleOfStock = 2.19;

    /// <summary>
    /// Where the map demonstrably stops streaming in time, as a multiple of stock.
    ///
    /// Where degradation first becomes visible at all, measured at 2.30x: individual modded objects
    /// pop in rather than areas — fences flickering while driving past, distant buildings taking
    /// about four seconds to resolve, close ones a fraction of a second, and only on the specific
    /// objects that were replaced. Everything else, including the unmodded parts of the same
    /// district, is normal.
    ///
    /// This is bounded tightly on both sides: 2.19x showed nothing at all, 2.30x showed this. It is
    /// the start of amber.
    /// </summary>
    public const double LocalFailureMultipleOfStock = 2.3;

    /// <summary>
    /// The heaviest weight still measured as amber rather than red: 3.05x, where Grove Street and
    /// the whole drive out to the replaced region were normal, and only the region itself broke down.
    ///
    /// The band below this is not uniform — 2.30x was individual objects popping in, 2.45x was whole
    /// streets not appearing until you stopped and looked at them for several seconds, and by 3.05x
    /// the affected patch had grown. What holds across all of it is the thing that matters to
    /// someone deciding: the damage stays where the mod is.
    /// </summary>
    public const double AmberTopMultipleOfStock = 3.05;

    /// <summary>
    /// Where trouble stops being confined to the replaced content, measured at 3.19x: objects were
    /// popping in and out of detail in Grove Street, which has no replaced texture anywhere near it,
    /// while the replaced region itself was unplayable — roads with no texture at all, obstacles
    /// invisible until they were hit, buildings taking 2-5 seconds even up close.
    ///
    /// This constant read 3.9x until 3.19x was actually run, which is the whole argument for testing
    /// rather than interpolating: map-wide failure starts far lower than the single sample above it
    /// implied. Where between 2.45x and 3.19x it really begins is still open.
    /// </summary>
    public const double GlobalFailureMultipleOfStock = 3.19;

    /// <summary>
    /// Where the game stopped staying up at all, measured at 3.98x: rendering had already failed
    /// map-wide, and it crashed on reaching the replaced region.
    ///
    /// Quoted in the advice but never used as a threshold — by this point the verdict is already as
    /// severe as it goes.
    /// </summary>
    public const double CrashMultipleOfStock = 3.98;

    /// <summary>
    /// How far above stock a game has to be before SAFT points out that it is already heavily
    /// modded.
    ///
    /// Anchored on <see cref="PlayedFineMultipleOfStock"/>: below that figure the game has actually
    /// been played and found normal, so saying "already heavily modded" there would be warning about
    /// a state known to be fine.
    /// </summary>
    public const double AlreadyModdedMultiplier = PlayedFineMultipleOfStock;

    /// <summary>
    /// Shown when a mod is within range. Deliberately reassuring rather than cautionary: the earlier
    /// wording repeated the "avoid HQ mods" advice even on a mod that passed, which read as though
    /// the user had done something wrong when they hadn't.
    /// </summary>
    private const string WithinRangeReassurance =
        "If anything goes wrong with your game after this mod, it's unlikely to be related to mod " +
        "byte size or density.";

    /// <summary>Shown only when a mod is flagged, where the advice is actually actionable.</summary>
    private const string LowQualityAdvice =
        "SAFT is best used with LQ (Low Quality) mods and SA-style mods that are not more detailed " +
        "than the original game's assets. Try to avoid using High Quality mods with SAFT, unless " +
        "you are OK with environments not rendering.";

    /// <summary>
    /// Sizes in the unit that actually says something. A small mod rounded to "0.0 MB" reads like a
    /// bug or like SAFT failed to measure anything, when the real answer is that it's genuinely tiny.
    /// </summary>
    private static string Mb(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    /// <summary>
    /// <paramref name="gameBaseline"/> is the player's game as it stands. Everything else here
    /// judges the MOD; this judges what it is being added to, which is the one thing the rest of the
    /// report quietly assumes is normal.
    /// </summary>
    public static StreamingVerdict Compose(
        PlacementDensityReport? density,
        StreamingImpactReport? impact,
        GameDensityBaseline? gameBaseline = null)
    {
        var lines = new List<string>();
        var withinRange = true;
        var severe = false;

        void Judge(double multiplier)
        {
            if (multiplier > DynamicWeightMultiplierLimit) withinRange = false;
            if (multiplier > SevereWeightMultiplier) severe = true;
        }

        if (density?.Densest is { } densest && density.TotalPlacements > 0)
        {
            lines.Add(
                $"This mod places {densest.ObjectCount} object(s) ({Mb(densest.Bytes)}) within one 200m area. " +
                $"The densest area in your own game holds {density.Baseline.BusiestObjectCount} object(s) " +
                $"({Mb(density.Baseline.BusiestBytes)}).");

            if (!density.WithinGameProvenRange) withinRange = false;
        }

        if (impact is not null && impact.IncreasesLoad)
        {
            if (impact.ReplacedDynamicModels > 0 && impact.DynamicWeightAfter > impact.DynamicWeightBefore)
            {
                // "also" only reads correctly when something was said before it — a replacement-only
                // mod has no density line, and opening with "It also" would be odd.
                var lead = lines.Count > 0 ? "It also makes" : "This mod makes";
                lines.Add(
                    $"{lead} {impact.ReplacedDynamicModels} vehicle/ped/weapon model(s) heavier: " +
                    $"{Mb(impact.DynamicWeightBefore)} becomes {Mb(impact.DynamicWeightAfter)} " +
                    $"({impact.DynamicMultiplier:0.0}x). These are not placed anywhere on the map — the game " +
                    "spawns them — so the extra weight applies everywhere at once.");

                Judge(impact.DynamicMultiplier);
            }

            if (impact.ReplacedPlacedModels > 0 && impact.HeaviestAreaAfter > impact.HeaviestAreaBefore)
            {
                lines.Add(
                    $"Replacements make the heaviest area of your map go from {Mb(impact.HeaviestAreaBefore)} " +
                    $"to {Mb(impact.HeaviestAreaAfter)} ({impact.AreaMultiplier:0.0}x).");

                // Deliberately NOT judged on the size of the jump. The placed-map side is the one side
                // that has now been measured directly, and it came out absolute: a game taken from 1.0x
                // to 1.8x stock - a 1.8x jump - played normally everywhere, while a game already sitting
                // high that moved barely at all still broke. What the engine has to stream in one area
                // is the thing that decides it, not how far it travelled to get there, so the absolute
                // check further down owns this.
            }
        }

        // Whether the MOD moved anything, decided before the absolute check below can add a line of
        // its own. A pack that changes nothing still deserves to be told it changes nothing.
        if (lines.Count == 0)
            lines.Add("This mod does not measurably increase what your game has to load.");

        // Everything above compares the mod against the game it is going into. That is the right
        // question only when the game is stock. Installed onto an already-heavy game, a mod can be a
        // small step up by that measure while the RESULT is far past what the engine streams: a pack
        // measured as no heavier than the game it replaced still broke that district, because the
        // absolute figure it produced was 2.4x stock. So the finished state is judged on its own.
        var heaviestAfter = AbsoluteHeaviestAfter(density, impact, gameBaseline);
        var multipleOfStock = heaviestAfter > 0 ? heaviestAfter / (double)StockHeaviestAreaBytes : 0;

        var pastMeasuredCeiling = multipleOfStock >= LocalFailureMultipleOfStock;
        if (pastMeasuredCeiling)
        {
            withinRange = false;
            if (multipleOfStock >= GlobalFailureMultipleOfStock) severe = true;
            lines.Add(MeasuredCeilingNote(heaviestAfter, multipleOfStock));
        }

        // Judged on where the install LANDS, for the same reason as the ceiling above: a mod that
        // lightens a bloated game would otherwise be warned about on the strength of the bloat it is
        // removing. Above the failure line this is silent, because the ceiling note has already said
        // it with better numbers.
        var landsInUntestedGap = multipleOfStock > PlayedFineMultipleOfStock &&
                                 multipleOfStock < LocalFailureMultipleOfStock;

        // Skipped when the note above already said it. "This mod MAY fall outside the range and will
        // MOST LIKELY show issues" is the right hedge for a weight nobody has run, and the wrong one
        // directly beneath a paragraph reporting what happened when somebody did.
        if (!pastMeasuredCeiling) lines.Add(Ramifications(withinRange, severe));

        // The reassurance is withheld on an already-heavy game: telling someone their trouble is
        // "unlikely to be related to byte size or density" directly above a note saying their game is
        // loaded down with exactly that would be two answers to the same question.
        if (!withinRange) lines.Add(LowQualityAdvice);
        else if (!landsInUntestedGap) lines.Add(WithinRangeReassurance);

        var severity = Severity(withinRange, severe);

        // Said last, because it reframes everything above it: "within range" means the MOD is not
        // the problem, and that is a much weaker statement when the game it lands in is carrying
        // more than anyone has actually run.
        if (landsInUntestedGap)
        {
            lines.Add(UntestedWeightNote(heaviestAfter, multipleOfStock));

            // A green tick would contradict the paragraph next to it. Never downgrades a worse
            // verdict — a mod that is genuinely too heavy stays red.
            if (severity == StreamingSeverity.Fine) severity = StreamingSeverity.Caution;
        }

        return new StreamingVerdict(withinRange, string.Join("\n\n", lines), severity);
    }

    /// <summary>
    /// What the heaviest 200m area of the map will hold once this mod is in, in absolute bytes.
    ///
    /// Taken from the strongest measurement available rather than one fixed source: a replacement
    /// mod has a directly measured after-figure, an addition mod does not, and for that case the
    /// worst realistic outcome is its own cluster landing in the area that is already the heaviest.
    /// That is an upper bound rather than a reading, which is the safe direction for a ceiling.
    /// </summary>
    private static long AbsoluteHeaviestAfter(
        PlacementDensityReport? density, StreamingImpactReport? impact, GameDensityBaseline? gameBaseline)
    {
        // The measured after-figure wins outright when there is one. It used to be floored at the
        // game's current weight on the reasoning that a ceiling should never read low - but that
        // makes a mod which LIGHTENS the game impossible to report, because the floor is the very
        // weight being removed. A pack that took a game from 2.4x down to 1.9x was still shown as
        // 2.4x and still went amber, describing a state the install was about to end.
        var afterReplacements = impact?.HeaviestAreaAfter ?? 0;
        if (afterReplacements <= 0)
            afterReplacements = Math.Max(gameBaseline?.BusiestBytes ?? 0, impact?.HeaviestAreaBefore ?? 0);

        // Only PLACED additions land in an area; a mod that defines objects without placing them
        // adds nothing to any one cell.
        var addedInOneArea = density?.Densest?.Bytes ?? 0;

        return afterReplacements + addedInOneArea;
    }

    /// <summary>
    /// The measured ceiling, quoted as what was actually seen rather than as a rule. These three
    /// figures come from playing the game at each of them on the hardware SAFT targets, which is the
    /// only reason this paragraph is allowed to state them as fact.
    /// </summary>
    /// <summary>
    /// The finding, as three bands rather than as a narrative of the runs that produced it.
    ///
    /// Four things this wording has to get right, all of them learned by getting them wrong first:
    ///
    /// It has to say WHOSE experience it is describing. Sentences like "at that weight the buildings
    /// stopped appearing" read either as laws of the engine or as predictions about the reader's own
    /// game. They were neither — they were runs on one device.
    ///
    /// It has to be about the reader's decision, not about the experiment. An earlier version walked
    /// through what happened at each weight in turn, which is the right shape for a lab notebook and
    /// the wrong one for someone holding a mod and a Continue button.
    ///
    /// It has to define its own terms. "Weight" is not obvious, and the reader cannot judge whether
    /// any of this applies to them without knowing it means density multiplied by file size.
    ///
    /// And it has to name the band the reader is in, in the same words as the list, so placing
    /// yourself takes no arithmetic.
    /// </summary>
    private static string MeasuredCeilingNote(long heaviestAfter, double multipleOfStock)
    {
        var band = multipleOfStock >= GlobalFailureMultipleOfStock ? "RED" : "AMBER";

        return $"After this mod, the heaviest 200m area of your map holds {Mb(heaviestAfter)} - " +
               $"{multipleOfStock:0.0}x what a stock copy of San Andreas holds there ({Mb(StockHeaviestAreaBytes)}).\n\n" +

               "Divinakra, SAFT's developer, tested mods across a range of weights and found that they fall " +
               "into three bands. Weight here means how much a mod adds to what the game has to load in one " +
               "place - how densely its objects sit, multiplied by how big its files are.\n\n" +

               $"GREEN, up to {PlayedFineMultipleOfStock:0.0}x vanilla - renders perfectly everywhere.\n\n" +

               $"AMBER, {LocalFailureMultipleOfStock:0.0}x to {AmberTopMultipleOfStock:0.0}x - rendering breaks " +
               "in the specific areas the mod loads heaviest, and the rest of the game stays perfect.\n\n" +

               $"RED, above {GlobalFailureMultipleOfStock:0.0}x - rendering breaks everywhere you go. The game " +
               "still plays, but things load without detail or stay invisible. Around " +
               $"{CrashMultipleOfStock:0.0}x it can also crash.\n\n" +

               $"Your game lands at {multipleOfStock:0.0}x - {band}. Every mod puts its weight somewhere " +
               "different, so treat these as the shape of the problem rather than exact lines.";
    }

    /// <summary>Whether a game at this weight is carrying more than a stock one by a wide margin.</summary>
    public static bool IsAlreadyHeavilyModded(GameDensityBaseline? gameBaseline) =>
        gameBaseline is not null &&
        gameBaseline.BusiestBytes > StockHeaviestAreaBytes * AlreadyModdedMultiplier;

    /// <summary>
    /// Shown for an install that lands in the gap between the heaviest weight anyone has played and
    /// found normal and the lightest weight anyone has seen fail. Saying nothing there would imply it
    /// was tested; saying it will break would be inventing a result. So it says which it is.
    /// </summary>
    private static string UntestedWeightNote(long heaviestAfter, double multipleOfStock) =>
        $"Note: this leaves your game carrying more than a stock one. Its heaviest area will hold " +
        $"{Mb(heaviestAfter)} ({multipleOfStock:0.0}x), where a stock copy of San Andreas holds about " +
        $"{Mb(StockHeaviestAreaBytes)} there. That is above the heaviest weight that has been played and " +
        $"found normal ({PlayedFineMultipleOfStock:0.0}x) but below the lightest one measured to break " +
        $"rendering ({LocalFailureMultipleOfStock:0.0}x) - so this is untested ground rather than " +
        "known-good or known-bad.";

    /// <summary>
    /// The plain-language consequence, in three tiers rather than two. The middle tier exists
    /// because a mod measured at 2.01x broke rendering only in the densest district and nowhere
    /// else — telling that user their mod is simply "outside the range" would overstate it, while
    /// calling it safe would be the promise SAFT must not break.
    /// </summary>
    private static StreamingSeverity Severity(bool withinRange, bool severe) =>
        withinRange ? StreamingSeverity.Fine : severe ? StreamingSeverity.Serious : StreamingSeverity.Caution;

    private static string Ramifications(bool withinRange, bool severe)
    {
        if (withinRange)
        {
            return "This mod is therefore within the range of what will perform well in GTA SA using " +
                   "only file addition/replacement via SAFT.";
        }

        if (!severe)
        {
            // Deliberately covers both routes into this tier: a replacement mod that only strains
            // dense districts, and an addition mod whose own cluster IS the dense area.
            return "This mod may fall outside the range of what will perform well in GTA SA using only " +
                   "file addition/replacement via SAFT, but will most likely only show issues in the " +
                   "densest areas — downtown, or wherever this mod concentrates its own objects — with " +
                   "things failing to render there while the rest of the map is fine.";
        }

        return "This mod is therefore outside the range of what will perform well in GTA SA using only " +
               "file addition/replacement via SAFT. Installing it is likely to cause world objects to " +
               "stop rendering throughout the game, not only in dense areas.";
    }
}
