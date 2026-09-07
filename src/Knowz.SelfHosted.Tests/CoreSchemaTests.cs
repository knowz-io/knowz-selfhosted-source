using Knowz.Core.Schema;

namespace Knowz.SelfHosted.Tests;

public class CoreSchemaTests
{
    [Fact]
    public void Version_Is_2()
    {
        Assert.Equal(2, CoreSchema.Version);
    }

    [Fact]
    public void MinReadableVersion_Is_1()
    {
        Assert.Equal(1, CoreSchema.MinReadableVersion);
    }

    [Fact]
    public void CanRead_CurrentVersion_ReturnsTrue()
    {
        Assert.True(CoreSchema.CanRead(2));
    }

    [Fact]
    public void CanRead_V1_ReturnsTrue()
    {
        Assert.True(CoreSchema.CanRead(1));
    }

    [Fact]
    public void CanRead_BelowMinimum_ReturnsFalse()
    {
        Assert.False(CoreSchema.CanRead(0));
    }

    [Fact]
    public void CanRead_AboveCurrent_ReturnsFalse()
    {
        Assert.False(CoreSchema.CanRead(3));
    }

    [Fact]
    public void CanRead_NegativeVersion_ReturnsFalse()
    {
        Assert.False(CoreSchema.CanRead(-1));
    }

    [Fact]
    public void GetCompatibilityInfo_WhenMinDiffersFromVersion_ReturnsRange()
    {
        // When MinReadableVersion != Version, format is "Schema v{Version} (reads v{Min}-v{Version})"
        Assert.Equal("Schema v2 (reads v1-v2)", CoreSchema.GetCompatibilityInfo());
    }
}
