using Aurum.App.SharedKernel.Common;

namespace Aurum.Api.Tests.Common;

public class PaginationHelperTests
{
    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 50)]
    [InlineData(-5, 50)]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    public void ClampPageSize_ClampsIntoTheAllowedRange(int? requested, int expected)
    {
        Assert.Equal(expected, PaginationHelper.ClampPageSize(requested));
    }
}
