using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Assemblies in Parse PDF into Bulk Add.</summary>
public class PdfImportAssemblyTests
{
    private static HardwareImportGroup G(string code, string generic, string catalog, params string[] sets)
    {
        var g = new HardwareImportGroup
        {
            ManufacturerCode = code, GenericName = generic, Catalog = catalog,
            SourceKey = ImportText.Key(catalog), Status = ImportMatchStatus.NeedsReview,
        };
        foreach (var s in sets) g.Lines.Add(new ParsedHardwareLine { ManufacturerCode = code, Catalog = catalog, Quantity = 1, SetName = s });
        return g;
    }

    [Fact]
    public void CandidatesComeFromTheSameHeading()
    {
        var device = G("VO", "Exit Device", "99L-F 996L-R", "080-TYP");
        var mullion = G("VO", "Mullion", "1690", "080-TYP");
        var strike = G("VO", "Strike", "6300", "080-TYP", "090");
        var hinge = G("IV", "Hinges", "5BB1 4 1/2 x 4 1/2", "090");
        var all = new[] { device, mullion, strike, hinge };

        Assert.Equal(new[] { mullion, strike }, HardwareImportAssemblies.Candidates(device, all, allHeadings: false));
        Assert.Equal(new[] { mullion, strike, hinge }, HardwareImportAssemblies.Candidates(device, all, allHeadings: true));

        // Costing reports: the same opening in two "Openings:" lists counts as the same heading.
        var a = G("AC01", "Lock", "9525ML.212", "1(3), CUSTOM(2)");
        var b = G("AC01", "Stop", "18DS", "1(3), 2C, 2.3(4)");
        var c = G("ZE", "Seal", "188S-BK", "4(2)");
        Assert.Equal(new[] { b }, HardwareImportAssemblies.Candidates(a, new[] { a, b, c }, allHeadings: false));
    }

    [Fact]
    public void ApplySetsTheLabelAndLeavesThePartsOut()
    {
        var device = G("VO", "Exit Device", "99L-F 996L-R", "080");
        var mullion = G("VO", "Mullion", "1690", "080");
        var strike = G("VO", "Strike", "6300", "080");
        device.Label = "99L-F 996L-R";

        HardwareImportAssemblies.Apply(device, new[] { mullion, strike }, "Exit Device Assembly");

        Assert.True(device.IsAssembly);
        Assert.Equal("Exit Device Assembly | 99L-F 996L-R | 1690 | 6300", device.Label);
        Assert.Same(device, mullion.PartOfAssembly);
        Assert.False(HardwareImportSession.InPlay(mullion));
        Assert.True(HardwareImportSession.InPlay(device));
        Assert.Empty(HardwareImportAssemblies.Candidates(G("XX", "x", "y", "080"), new[] { device, mullion, strike }, false)
            .Where(g => g == device));

        // Blank description = callouts only; changing the parts replaces them.
        HardwareImportAssemblies.Apply(device, new[] { strike }, "");
        Assert.Equal("99L-F 996L-R | 6300", device.Label);
        Assert.Null(mullion.PartOfAssembly);

        // No parts: not an assembly any more, label back to the callout.
        HardwareImportAssemblies.Apply(device, Array.Empty<HardwareImportGroup>(), "");
        Assert.False(device.IsAssembly);
        Assert.Null(strike.PartOfAssembly);
        Assert.Equal("99L-F 996L-R", device.Label);
    }

    [Fact]
    public void RemembersAndSuggestsAssemblies()
    {
        var device = G("VO", "Exit Device", "99L-F 996L-R", "080");
        var mullion = G("VO", "Mullion", "1690", "080");
        HardwareImportAssemblies.Apply(device, new[] { mullion }, "Exit Device Assembly");
        var learned = new List<LearnedAssembly>();
        HardwareImportAssemblies.Record(learned, new[] { device });
        HardwareImportAssemblies.Record(learned, new[] { device });
        Assert.Equal(2, Assert.Single(learned).Count);

        // A new PDF with the same callouts in one heading: suggested, not applied.
        var device2 = G("VO", "Exit Device", "99L-F 996L-R", "101");
        var mullion2 = G("VO", "Mullion", "1690", "101");
        var other = G("VO", "Mullion", "1690", "999");
        HardwareImportAssemblies.Suggest(new[] { device2, mullion2 }, learned);
        Assert.Equal(new[] { mullion2 }, device2.SuggestedAssemblyParts);
        Assert.Equal("Exit Device Assembly", device2.SuggestedAssemblyDescription);
        Assert.False(device2.IsAssembly);
        Assert.Null(other.PartOfAssembly);
    }
}
