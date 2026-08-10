using System.Globalization;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class IplFileTests
{
    [Fact]
    public void Parses_instance_lines_and_ignores_other_sections()
    {
        var lines = new[]
        {
            "# IPL generated from Max file LAe2.max",
            "inst",
            "17512, LODgwforum1_LAe, 0, 2737.75, -1760.0625, 26.2265625, 0, 0, -8.742277657e-008, 1, -1",
            "714, veg_bevtree2, 0, 2356.53125, -1192.71875, 26.1640625, 0, 0, -0.7398733497, 0.6727461815, -1",
            "end",
            "cull",
            "2222.5, -1193.5, 20.5, 100, 100, 100, 1, 0, 0, 0, 0",
            "end",
        };

        var instances = IplFile.ParseLines(lines);

        // Only "inst" lines are placements; cull/zone/enex describe regions and triggers, and
        // treating one as an object would invent placements that don't exist.
        Assert.Equal(2, instances.Count);
        Assert.Equal(17512, instances[0].ObjectId);
        Assert.Equal("LODgwforum1_LAe", instances[0].ModelName);
        Assert.Equal(2737.75, instances[0].X, 3);
        Assert.Equal(-1760.0625, instances[0].Y, 3);
        Assert.Equal(26.2265625, instances[0].Z, 3);
    }

    [Fact]
    public void Parses_scientific_notation_coordinates_regardless_of_machine_locale()
    {
        // The game writes near-zero rotation components as "-8.742277657e-008". A locale that uses
        // a comma decimal separator would otherwise read these files as garbage, so parsing is
        // pinned to invariant culture.
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            var instances = IplFile.ParseLines(new[]
            {
                "inst",
                "17512, LODgwforum1_LAe, 0, 2737.75, -1760.0625, 26.2265625, 0, 0, -8.742277657e-008, 1, -1",
                "end",
            });

            var instance = Assert.Single(instances);
            Assert.Equal(2737.75, instance.X, 3);
            Assert.Equal(26.2265625, instance.Z, 3);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Formats_a_placement_line_in_invariant_form()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            var line = IplFile.FormatInstance(12000, "saftcastle", 2495.5, -1690.25, 14.0);

            // Must be "2495.5" and never "2495,5" — a comma would split the field and corrupt the file.
            Assert.Equal("12000, saftcastle, 0, 2495.5, -1690.25, 14, 0, 0, 0, 1, -1", line);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_formatted_line_can_be_read_back_by_the_parser()
    {
        var line = IplFile.FormatInstance(12000, "saftcastle", 2495.5, -1690.25, 14.0);

        var instance = Assert.Single(IplFile.ParseLines(new[] { "inst", line, "end" }));

        Assert.Equal(12000, instance.ObjectId);
        Assert.Equal("saftcastle", instance.ModelName);
        Assert.Equal(2495.5, instance.X, 3);
        Assert.Equal(-1690.25, instance.Y, 3);
        Assert.Equal(14.0, instance.Z, 3);
    }
}
