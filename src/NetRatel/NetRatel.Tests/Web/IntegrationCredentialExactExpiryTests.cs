using System.Globalization;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class IntegrationCredentialExactExpiryTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void Exact_instant_round_trips_and_thirty_days_is_720_hours(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var requested = DateTimeOffset.Parse("2030-04-01T10:11:12.1234567Z", CultureInfo.InvariantCulture);
            Assert.Equal(requested, IntegrationCredentialExpiry.ParseUtcInstant(IntegrationCredentialExpiry.Format(requested)));
            var reference = new DateTimeOffset(2030, 3, 2, 12, 11, 12, TimeSpan.FromHours(2));
            var expiry = IntegrationCredentialExpiry.ThirtyDaysFrom(reference);
            Assert.Equal(TimeSpan.FromHours(720), expiry - reference);
            Assert.Equal("2030-04-01T10:11:12.0000000Z", IntegrationCredentialExpiry.Format(expiry));
            Assert.Null(IntegrationCredentialExpiry.ParseUtcInstant("2030-04-01"));
            Assert.Null(IntegrationCredentialExpiry.ParseUtcInstant("2030-04-01T10:11:12+02:00"));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
