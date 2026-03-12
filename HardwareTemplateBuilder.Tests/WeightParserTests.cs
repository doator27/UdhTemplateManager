using HardwareTemplateBuilder.Core.Services;
using System;
using Xunit;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Unit tests for <see cref="WeightParser"/>.</summary>
public class WeightParserTests
{
    private readonly WeightParser _parser = new();

    [Fact]
    public void Parse_ValidWeight_ReturnsCorrectSegments()
    {
        var result = _parser.Parse("01.002.015");
        Assert.Equal(1, result.Function);
        Assert.Equal(2, result.Type);
        Assert.Equal(15, result.SubType);
    }

    [Fact]
    public void Parse_ZeroWeight_ReturnsZeroSegments()
    {
        var result = _parser.Parse("00.000.000");
        Assert.Equal(0, result.Function);
        Assert.Equal(0, result.Type);
        Assert.Equal(0, result.SubType);
    }

    [Fact]
    public void Parse_MaxSegments_ReturnsCorrectValues()
    {
        var result = _parser.Parse("99.999.999");
        Assert.Equal(99, result.Function);
        Assert.Equal(999, result.Type);
        Assert.Equal(999, result.SubType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ThrowsFormatException(string input)
    {
        Assert.Throws<FormatException>(() => _parser.Parse(input));
    }

    [Theory]
    [InlineData("01.002")]          // only 2 segments
    [InlineData("01.002.003.004")]  // 4 segments
    [InlineData("abc.002.003")]     // non-numeric
    [InlineData("01.abc.003")]
    [InlineData("01.002.abc")]
    public void Parse_InvalidFormat_ThrowsFormatException(string input)
    {
        Assert.Throws<FormatException>(() => _parser.Parse(input));
    }

    [Fact]
    public void ParsedWeight_CompareTo_SortsCorrectly()
    {
        var a = _parser.Parse("01.001.001");
        var b = _parser.Parse("01.001.002");
        var c = _parser.Parse("01.002.001");
        var d = _parser.Parse("02.001.001");

        Assert.True(a.CompareTo(b) < 0); // a < b
        Assert.True(b.CompareTo(a) > 0); // b > a
        Assert.True(b.CompareTo(c) < 0); // b < c
        Assert.True(c.CompareTo(d) < 0); // c < d
        Assert.Equal(0, a.CompareTo(_parser.Parse("01.001.001"))); // a == a
    }

    [Fact]
    public void Compare_TwoStrings_ReturnsCorrectOrder()
    {
        Assert.True(_parser.Compare("01.001.001", "01.001.002") < 0);
        Assert.True(_parser.Compare("02.000.000", "01.999.999") > 0);
        Assert.Equal(0, _parser.Compare("03.005.010", "03.005.010"));
    }
}
