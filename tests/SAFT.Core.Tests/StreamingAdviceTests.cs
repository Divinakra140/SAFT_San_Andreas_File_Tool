using SAFT.Core;

namespace SAFT.Core.Tests;

public class StreamingAdviceTests
{
    private static readonly GameDensityBaseline Baseline = new(BusiestObjectCount: 171, BusiestBytes: 8_600_000);

    private static PlacementDensityReport Density(int objects, long bytes) =>
        new(objects, new DensestArea(2500, -1690, objects, bytes), Baseline);

    private static StreamingImpactReport Impact(
        long areaBefore = 0, long areaAfter = 0, long dynBefore = 0, long dynAfter = 0,
        int placed = 0, int dynamic = 0) =>
        new(areaBefore, areaAfter, dynBefore, dynAfter, placed, dynamic);

    [Fact]
    public void A_modest_mod_is_within_range_and_only_needs_acknowledging()
    {
        var verdict = StreamingAdvice.Compose(Density(4, 250_000), impact: null);

        Assert.True(verdict.WithinRange);
        Assert.False(verdict.NeedsConfirmation);
        Assert.Equal(StreamingSeverity.Fine, verdict.Severity);
        Assert.Contains("within the range of what will perform well", verdict.Message);

        // A mod that passed shouldn't be lectured about avoiding HQ mods — that read as though the
        // user had done something wrong when they hadn't. Reassurance instead.
        Assert.Contains("unlikely to be related to mod byte size or density", verdict.Message);
        Assert.DoesNotContain("LQ (Low Quality) mods", verdict.Message);
    }

    [Fact]
    public void Flagged_mods_still_get_the_low_quality_advice_where_it_is_actionable()
    {
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 939_200_000, dynamic: 212));

        Assert.Contains("LQ (Low Quality) mods", verdict.Message);
        Assert.Equal(StreamingSeverity.Serious, verdict.Severity);
    }

    [Fact]
    public void Severity_matches_the_three_tiers()
    {
        Assert.Equal(StreamingSeverity.Fine,
            StreamingAdvice.Compose(Density(4, 250_000), null).Severity);

        Assert.Equal(StreamingSeverity.Caution,
            StreamingAdvice.Compose(null, Impact(dynBefore: 99_300_000, dynAfter: 199_500_000, dynamic: 48)).Severity);

        Assert.Equal(StreamingSeverity.Serious,
            StreamingAdvice.Compose(null, Impact(dynBefore: 99_300_000, dynAfter: 939_200_000, dynamic: 212)).Severity);
    }

    [Fact]
    public void Warns_when_the_game_being_installed_into_is_already_heavily_modded()
    {
        // The mod-side report compares against the player's CURRENT game. Someone who has already
        // filled theirs with HQ replacements gets a green tick for the next one, because it is being
        // graded against their own bloat. Saying so is the point of this note. At 2.25x the game sits
        // in the gap between the heaviest weight played and found normal and the lightest one
        // measured to fail, so the note has to say that without claiming a result either way.
        var heavy = new GameDensityBaseline(
            BusiestObjectCount: 1_186,
            BusiestBytes: (long)(StreamingAdvice.StockHeaviestAreaBytes * 2.25));

        var verdict = StreamingAdvice.Compose(Density(4, 250_000), impact: null, gameBaseline: heavy);

        Assert.Contains("leaves your game carrying more than a stock one", verdict.Message);
        Assert.Contains("untested ground rather than known-good or known-bad", verdict.Message);

        // And the reassurance is withheld: "unlikely to be related to byte size or density" sitting
        // above a note about the game being loaded with exactly that is two answers to one question.
        Assert.DoesNotContain("unlikely to be related to mod byte size", verdict.Message);

        // Amber, not green: a tick would contradict the paragraph beside it. Still only an
        // acknowledgement, since the MOD itself is fine.
        Assert.Equal(StreamingSeverity.Caution, verdict.Severity);
        Assert.True(verdict.WithinRange);
        Assert.False(verdict.NeedsConfirmation);
    }

    [Fact]
    public void A_stock_game_gets_no_extra_warning_and_keeps_its_green_tick()
    {
        var stock = new GameDensityBaseline(
            BusiestObjectCount: 1_186, BusiestBytes: StreamingAdvice.StockHeaviestAreaBytes);

        var verdict = StreamingAdvice.Compose(Density(4, 250_000), impact: null, gameBaseline: stock);

        Assert.DoesNotContain("heavily modded", verdict.Message);
        Assert.Equal(StreamingSeverity.Fine, verdict.Severity);
    }

    [Fact]
    public void The_already_modded_note_never_softens_a_worse_verdict()
    {
        var heavy = new GameDensityBaseline(
            BusiestObjectCount: 1_186,
            BusiestBytes: (long)(StreamingAdvice.StockHeaviestAreaBytes * 2.25));

        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 939_200_000, dynamic: 212),
            gameBaseline: heavy);

        Assert.Contains("leaves your game carrying more than a stock one", verdict.Message);
        Assert.Equal(StreamingSeverity.Serious, verdict.Severity);   // stays red
    }

    [Fact]
    public void Even_a_mod_that_changes_nothing_reports_an_already_heavy_game()
    {
        // This path used to return early with a one-line message, which skipped the note entirely.
        var heavy = new GameDensityBaseline(
            BusiestObjectCount: 1_186,
            BusiestBytes: (long)(StreamingAdvice.StockHeaviestAreaBytes * 2.25));

        var verdict = StreamingAdvice.Compose(density: null, impact: null, gameBaseline: heavy);

        Assert.Contains("does not measurably increase", verdict.Message);
        Assert.Contains("leaves your game carrying more than a stock one", verdict.Message);
        Assert.Equal(StreamingSeverity.Caution, verdict.Severity);
    }

    [Fact]
    public void An_over_dense_mod_is_outside_range_and_asks_before_installing()
    {
        var verdict = StreamingAdvice.Compose(Density(400, 2_000_000), impact: null);

        Assert.False(verdict.WithinRange);
        Assert.True(verdict.NeedsConfirmation);
        Assert.Contains("outside the range", verdict.Message);
        // An over-dense ADDITION lands in the middle tier, and the wording has to fit that case:
        // the risky area isn't downtown, it's the one this mod just created.
        Assert.Contains("wherever this mod concentrates its own objects", verdict.Message);
    }

    [Fact]
    public void The_car_pack_case_is_caught_even_with_no_additions_at_all()
    {
        // Replacement-only mod: nothing placed, nothing added, but every vehicle got heavier. This
        // is the 90s car pack — the exact mod that degraded rendering while SAFT said nothing.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 939_200_000, dynamic: 212));

        Assert.False(verdict.WithinRange);
        Assert.Contains("9.5x", verdict.Message);
        Assert.Contains("spawns them", verdict.Message);
        Assert.Contains("everywhere at once", verdict.Message);
        // Reads as an opening sentence, since a replacement-only mod has no density line before it.
        Assert.StartsWith("This mod makes", verdict.Message);
        Assert.DoesNotContain("It also makes", verdict.Message);
    }

    [Fact]
    public void Uses_also_only_when_a_sentence_came_before_it()
    {
        var verdict = StreamingAdvice.Compose(
            Density(10, 500_000),
            Impact(dynBefore: 99_300_000, dynAfter: 400_000_000, dynamic: 50));

        Assert.Contains("It also makes", verdict.Message);
    }

    [Fact]
    public void A_mild_replacement_stays_within_range()
    {
        // 1.3x heavier vehicles: more than vanilla, but comfortably under the line.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 129_000_000, dynamic: 40));

        Assert.True(verdict.WithinRange);
        Assert.Contains("within the range", verdict.Message);
    }

    [Fact]
    public void The_measured_weapons_pack_is_flagged_with_room_to_spare()
    {
        // NeXtGen Remaster Weapons, measured against a real install: 99.3 MB -> 199.5 MB. It broke
        // world rendering downtown, so it must never be reported as safe. The threshold sits well
        // below this rather than at it, because SAFT promises a mod works everywhere — including
        // the heaviest district — and the true failure line is somewhere under this figure.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 199_500_000, dynamic: 48));

        Assert.False(verdict.WithinRange);
        Assert.True(verdict.NeedsConfirmation);
    }

    [Fact]
    public void The_weapons_pack_gets_the_dense_areas_wording_not_the_everywhere_wording()
    {
        // 2.01x is itself the "only downtown" case, so it belongs in the middle tier: flagged, but
        // described accurately rather than as a game-wide catastrophe.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 199_500_000, dynamic: 48));

        Assert.False(verdict.WithinRange);
        Assert.Contains("only show issues in the densest areas", verdict.Message);
        Assert.Contains("downtown", verdict.Message);
        Assert.DoesNotContain("throughout the game", verdict.Message);
    }

    [Fact]
    public void The_car_pack_gets_the_everywhere_wording()
    {
        // 9.5x broke rendering constantly, all over the map — a different warning from the pack
        // that only misbehaved in one district.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 939_200_000, dynamic: 212));

        Assert.False(verdict.WithinRange);
        Assert.Contains("throughout the game", verdict.Message);
        Assert.DoesNotContain("only show issues in dense areas", verdict.Message);
    }

    [Fact]
    public void A_mod_just_under_the_measured_failure_is_still_flagged()
    {
        // 1.8x: below the pack we know broke downtown, but with no evidence it's safe there. The
        // conservative call is to ask rather than to promise.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(dynBefore: 99_300_000, dynAfter: 178_000_000, dynamic: 45));

        Assert.False(verdict.WithinRange);
    }

    [Fact]
    public void Says_so_plainly_when_a_mod_changes_nothing_about_the_load()
    {
        var verdict = StreamingAdvice.Compose(
            density: null, impact: Impact(dynBefore: 99_300_000, dynAfter: 99_300_000));

        Assert.True(verdict.WithinRange);
        Assert.Contains("does not measurably increase", verdict.Message);
    }

    [Fact]
    public void A_lighter_replacement_is_never_treated_as_a_risk()
    {
        // Swapping HD assets back out for smaller ones reduces the load; warning about that would
        // be nonsense.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(areaBefore: 8_600_000, areaAfter: 3_000_000, placed: 20));

        Assert.True(verdict.WithinRange);
    }

    [Fact]
    public void Both_kinds_of_risk_are_reported_together_when_a_mod_does_both()
    {
        var verdict = StreamingAdvice.Compose(
            Density(400, 30_000_000),
            Impact(dynBefore: 99_300_000, dynAfter: 500_000_000, dynamic: 100));

        Assert.False(verdict.WithinRange);
        Assert.Contains("within one 200m area", verdict.Message);   // the addition side
        Assert.Contains("vehicle/ped/weapon", verdict.Message);      // the replacement side
    }

    /// <summary>
    /// The bug this whole group exists for. A texture pack installed over an already-modded game was
    /// judged against that game rather than against a stock one, came out as a small step up, and was
    /// shown as acceptable — while the game it produced was past the point that had already been
    /// measured as breaking rendering.
    /// </summary>
    private static long Stock(double multiple) => (long)(StreamingAdvice.StockHeaviestAreaBytes * multiple);

    [Fact]
    public void A_mod_that_adds_nothing_relative_is_still_flagged_when_the_result_is_past_the_measured_ceiling()
    {
        // Replacing a heavy pack with an equally heavy one: no increase at all by the relative
        // measure, and the finished game still sits where rendering was measured to fail.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(areaBefore: Stock(2.5), areaAfter: Stock(2.5), placed: 30),
            gameBaseline: new GameDensityBaseline(1_186, Stock(2.5)));

        Assert.False(verdict.WithinRange);
        Assert.True(verdict.NeedsConfirmation);
        Assert.Contains("2.5x what a stock copy of San Andreas holds", verdict.Message);
        Assert.Contains("Your game lands at 2.5x", verdict.Message);
    }

    [Fact]
    public void Trouble_confined_to_the_mod_is_amber_and_trouble_that_spreads_is_red()
    {
        var local = StreamingAdvice.Compose(
            null, Impact(areaBefore: Stock(1.0), areaAfter: Stock(2.6), placed: 30), new GameDensityBaseline(1_186, Stock(1.0)));
        var global = StreamingAdvice.Compose(
            null, Impact(areaBefore: Stock(1.0), areaAfter: Stock(3.3), placed: 30), new GameDensityBaseline(1_186, Stock(1.0)));

        Assert.Equal(StreamingSeverity.Caution, local.Severity);
        Assert.Equal(StreamingSeverity.Serious, global.Severity);
    }

    [Fact]
    public void A_game_at_the_weight_that_was_played_and_found_normal_is_not_flagged()
    {
        // 1.8x was driven through its own district and the rest of the map with no difference of any
        // kind, so warning at or below it would be warning about a state known to be fine.
        var verdict = StreamingAdvice.Compose(
            null, Impact(areaBefore: Stock(1.0), areaAfter: Stock(1.8), placed: 30), new GameDensityBaseline(1_186, Stock(1.0)));

        Assert.True(verdict.WithinRange);
        Assert.Equal(StreamingSeverity.Fine, verdict.Severity);
        Assert.DoesNotContain("what a stock San Andreas holds there", verdict.Message);
    }

    [Fact]
    public void A_mod_that_makes_the_game_lighter_is_reported_at_where_it_lands_not_where_it_started()
    {
        // The ceiling used to be floored at the game's CURRENT weight, which made a mod that removes
        // load impossible to report: a pack taking a game from 2.4x down to 1.9x was shown as 2.4x
        // and went amber, describing the state the install was about to end.
        var verdict = StreamingAdvice.Compose(
            density: null,
            impact: Impact(areaBefore: Stock(2.4), areaAfter: Stock(1.9), placed: 30),
            gameBaseline: new GameDensityBaseline(1_186, Stock(2.4)));

        Assert.True(verdict.WithinRange);
        Assert.Equal(StreamingSeverity.Fine, verdict.Severity);
        Assert.DoesNotContain("what a stock San Andreas holds there", verdict.Message);
    }

    [Fact]
    public void The_measured_ceiling_is_quoted_as_something_that_was_played_rather_than_as_a_rule()
    {
        var verdict = StreamingAdvice.Compose(
            null, Impact(areaBefore: Stock(1.0), areaAfter: Stock(2.6), placed: 30), new GameDensityBaseline(1_186, Stock(1.0)));

        // Attributed, so nobody reads these as laws of the engine or as things that happened to
        // them. They are runs on one device and the reader is entitled to know that.
        Assert.Contains("Divinakra, SAFT's developer, tested mods", verdict.Message);
        Assert.Contains("Every mod puts its weight somewhere different", verdict.Message);

        // "Weight" is the term the whole message turns on, so it defines itself rather than assuming.
        Assert.Contains("how densely its objects sit, multiplied by how big its files are", verdict.Message);

        // All three bands, so a reader can place themselves rather than be handed one verdict.
        Assert.Contains("GREEN, up to 2.2x", verdict.Message);
        Assert.Contains("AMBER, 2.3x to 3.1x", verdict.Message);
        Assert.Contains("RED, above 3.2x", verdict.Message);
        Assert.Contains("Around 4.0x it can also crash", verdict.Message);
    }

    [Fact]
    public void The_reader_is_told_which_band_they_are_in_using_the_same_word_as_the_list()
    {
        var amber = StreamingAdvice.Compose(
            null, Impact(areaBefore: Stock(1.0), areaAfter: Stock(2.6), placed: 30), new GameDensityBaseline(1_186, Stock(1.0)));
        var red = StreamingAdvice.Compose(
            null, Impact(areaBefore: Stock(1.0), areaAfter: Stock(3.3), placed: 30), new GameDensityBaseline(1_186, Stock(1.0)));

        Assert.Contains("Your game lands at 2.6x - AMBER", amber.Message);
        Assert.Contains("Your game lands at 3.3x - RED", red.Message);
    }

    [Fact]
    public void The_advice_never_addresses_the_test_findings_as_the_readers_own_experience()
    {
        // The earlier wording said things like "the modded district's buildings stopped appearing on
        // approach", with no subject - which reads as a prediction about the reader's game rather
        // than a report of someone else's. Every claim now hangs off the attribution.
        foreach (var multiple in new[] { 2.35, 2.6, 3.3, 4.2 })
        {
            var verdict = StreamingAdvice.Compose(
                null, Impact(areaBefore: Stock(1.0), areaAfter: Stock(multiple), placed: 30),
                new GameDensityBaseline(1_186, Stock(1.0)));

            Assert.Contains("Divinakra, SAFT's developer", verdict.Message);
            Assert.DoesNotContain("modded district", verdict.Message);
        }
    }
}
