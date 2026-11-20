using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for type-to-jump in lists (<see cref="TypeAheadSearch"/>).</summary>
public class TypeAheadSearchTests
{
    private static readonly string[] Mfrs = { "Hager", "Ives", "LCN", "Norton Rixson", "Schlage", "Von Duprin", "Vonnegut", "Zero" };

    [Fact]
    public void TypingBuildsUpASearchAndAPauseStartsOver()
    {
        var s = new TypeAheadSearch();
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
        Assert.Equal("v", s.Append("v", t0));
        Assert.Equal("vo", s.Append("o", t0.AddMilliseconds(300)));
        Assert.Equal("l", s.Append("l", t0.AddSeconds(3)));
    }

    [Fact]
    public void FindsByStartThenByWord()
    {
        Assert.Equal(5, TypeAheadSearch.Find(Mfrs, -1, "vo"));
        Assert.Equal(6, TypeAheadSearch.Find(Mfrs, -1, "vonn"));
        Assert.Equal(5, TypeAheadSearch.Find(Mfrs, 5, "von"));      // stays put while it still matches
        Assert.Equal(3, TypeAheadSearch.Find(Mfrs, -1, "rix"));     // start of a word
        Assert.Equal(-1, TypeAheadSearch.Find(Mfrs, -1, "qq"));
    }

    [Fact]
    public void SameLetterCyclesThroughItems()
    {
        Assert.Equal(5, TypeAheadSearch.Find(Mfrs, -1, "v"));
        Assert.Equal(6, TypeAheadSearch.Find(Mfrs, 5, "vv"));
        Assert.Equal(5, TypeAheadSearch.Find(Mfrs, 6, "vvv"));      // wraps
        Assert.Equal(0, TypeAheadSearch.Find(Mfrs, 7, "h"));
    }

    private sealed record Tagged(string Name, int Id);

    private sealed class Plain
    {
        public string ModelNumber { get; set; } = "4040XP";
    }

    [Fact]
    public void ItemTextUsesTheBestAvailableText()
    {
        Assert.Equal("Von Duprin", TypeAheadSearch.ItemText(new Manufacturer { ManufacturerName = "Von Duprin" }, "ManufacturerName"));
        Assert.Equal("Von Duprin", TypeAheadSearch.ItemText(new Manufacturer { ManufacturerName = "Von Duprin" }));
        Assert.Equal("4040XP", TypeAheadSearch.ItemText(new Plain()));
        Assert.Equal("x", TypeAheadSearch.ItemText(new Tagged("x", 1)));   // a record's generated ToString isn't used
        Assert.Equal("abc", TypeAheadSearch.ItemText("abc"));
        Assert.Null(TypeAheadSearch.ItemText(null));
    }
}
