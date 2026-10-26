using EdgeRetails.Central.Application.Services;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public sealed class StorageDownloadGrantPolicyTests
{
    [Theory]
    [InlineData("1", true, 1)]
    [InlineData("120", true, 120)]
    [InlineData("300", true, 300)]
    [InlineData(null, false, 0)]
    [InlineData("", false, 0)]
    [InlineData("0", false, 0)]
    [InlineData("301", false, 301)]
    [InlineData("-1", false, -1)]
    [InlineData("abc", false, 0)]
    public void TryParseTtlSeconds_RequiresExplicitShortBoundedValue(string? input, bool expected, int expectedValue)
    {
        Assert.Equal(expected, StorageDownloadGrantPolicy.TryParseTtlSeconds(input, out var actual));
        Assert.Equal(expectedValue, actual);
    }
}
