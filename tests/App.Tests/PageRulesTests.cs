using App.Api.Endpoints;
using Xunit;

namespace App.Tests;

public class PageRulesTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 20)]
    [InlineData(1, 100)]
    [InlineData(21474836, 100)] // (page - 1) * size = 2147483600, the largest offset that still fits in int
    public void Validate_InRangeValues_ReturnsNoErrors(int page, int size)
    {
        Assert.Empty(PageRules.Validate(page, size));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(int.MinValue, 20)] // must not overflow when computing page - 1
    public void Validate_PageBelowOne_ReturnsPageError(int page, int size)
    {
        var errors = PageRules.Validate(page, size);
        Assert.True(errors.ContainsKey("page"));
        Assert.False(errors.ContainsKey("size"));
    }

    [Theory]
    [InlineData(30000001, 100)] // (page - 1) * size = 3000000000 overflows int
    [InlineData(int.MaxValue, 2)]
    [InlineData(int.MaxValue, 100)]
    public void Validate_OffsetBeyondIntRange_ReturnsPageError(int page, int size)
    {
        var errors = PageRules.Validate(page, size);
        Assert.True(errors.ContainsKey("page"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Validate_SizeOutOfRange_ReturnsSizeError(int size)
    {
        var errors = PageRules.Validate(1, size);
        Assert.True(errors.ContainsKey("size"));
        Assert.False(errors.ContainsKey("page"));
    }

    [Fact]
    public void Defaults_AreFirstPageOfTwenty()
    {
        Assert.Equal(1, PageRules.DefaultPage);
        Assert.Equal(20, PageRules.DefaultSize);
        Assert.Empty(PageRules.Validate(PageRules.DefaultPage, PageRules.DefaultSize));
    }
}
