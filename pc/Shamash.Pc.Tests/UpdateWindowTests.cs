using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class UpdateWindowTests
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [Fact]
    public void Parse_ValidWindow()
    {
        var w = UpdateWindow.Parse("01:30-04:15");
        Assert.Equal(T(1, 30), w.Start);
        Assert.Equal(T(4, 15), w.End);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("25:00-26:00")]
    [InlineData("03:00")]
    [InlineData("03:00-04:00-05:00")]
    public void Parse_MalformedFallsBackToDefault(string? text)
    {
        Assert.Equal(UpdateWindow.Default, UpdateWindow.Parse(text));
    }

    [Fact]
    public void Default_Is_0300_to_0500()
    {
        Assert.Equal(T(3), UpdateWindow.Default.Start);
        Assert.Equal(T(5), UpdateWindow.Default.End);
    }

    [Theory]
    [InlineData(2, 59, false)]
    [InlineData(3, 0, true)]
    [InlineData(4, 59, true)]
    [InlineData(5, 0, false)]
    public void Contains_SameDayWindow(int h, int m, bool expected)
    {
        Assert.Equal(expected, UpdateWindow.Default.Contains(T(h, m)));
    }

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(1, 59, true)]
    [InlineData(2, 0, false)]
    [InlineData(12, 0, false)]
    public void Contains_WindowCrossingMidnight(int h, int m, bool expected)
    {
        var w = UpdateWindow.Parse("22:00-02:00");
        Assert.Equal(expected, w.Contains(T(h, m)));
    }

    [Fact]
    public void Contains_EmptyWindowIsNeverOpen()
    {
        var w = UpdateWindow.Parse("03:00-03:00");
        Assert.False(w.Contains(T(3)));
    }
}
