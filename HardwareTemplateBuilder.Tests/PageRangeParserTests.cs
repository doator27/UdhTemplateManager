using HardwareTemplateBuilder.Core.Services;
using System;
using Xunit;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Unit tests for <see cref="PageRangeParser"/>.</summary>
public class PageRangeParserTests
{
    private readonly PageRangeParser _parser = new();

    [Fact]
    public void Parse_SinglePage_ReturnsSingleItem()
    {
        var result = _parser.Parse("1");
        Assert.Equal(new[] { 1 }, result);
    }

    [Fact]
    public void Parse_SimpleRange_ReturnsAllPagesInRange()
    {
        var result = _parser.Parse("2-5");
        Assert.Equal(new[] { 2, 3, 4, 5 }, result);
    }

    [Fact]
    public void Parse_CommaSeparated_ReturnsIndividualPages()
    {
        var result = _parser.Parse("1,3,5");
        Assert.Equal(new[] { 1, 3, 5 }, result);
    }

    [Fact]
    public void Parse_MixedRangeAndSingle_ReturnsCombined()
    {
        var result = _parser.Parse("1,3-5,8");
        Assert.Equal(new[] { 1, 3, 4, 5, 8 }, result);
    }

    [Fact]
    public void Parse_DuplicatePages_ReturnsDeduplicated()
    {
        var result = _parser.Parse("1,1,2-3,2");
        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void Parse_UnsortedInput_ReturnsSorted()
    {
        var result = _parser.Parse("8,1,3-5");
        Assert.Equal(new[] { 1, 3, 4, 5, 8 }, result);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var result = _parser.Parse("");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.Parse(null!));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1-")]
    [InlineData("-5")]
    [InlineData("3-1")]   // reversed range
    [InlineData("0")]     // page 0 is invalid
    [InlineData("1-2-3")] // double dash
    public void Parse_InvalidInput_ThrowsFormatException(string input)
    {
        Assert.Throws<FormatException>(() => _parser.Parse(input));
    }
}
