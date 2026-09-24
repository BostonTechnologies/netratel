using System.Globalization;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class IntegrationCredentialExpiryTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    public void CalendarDateAlwaysEndsAt2359UtcAcrossLocalesAndDateKinds(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Local, DateTimeKind.Utc })
            {
                var selected = new DateTime(2028, 2, 29, 9, 30, 0, kind);
                Assert.Equal(new DateTimeOffset(2028, 2, 29, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
                    IntegrationCredentialExpiry.EndOfUtcDay(selected));
            }
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public void YearBoundaryAndNoSelection()
    {
        Assert.Null(IntegrationCredentialExpiry.EndOfUtcDay(null));
        Assert.Equal(new DateTimeOffset(2028, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
            IntegrationCredentialExpiry.EndOfUtcDay(new DateTime(2028, 12, 31)));
    }
}
