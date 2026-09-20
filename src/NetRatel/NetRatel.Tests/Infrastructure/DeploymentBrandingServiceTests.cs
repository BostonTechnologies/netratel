using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Branding;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class DeploymentBrandingServiceTests
{
    [Fact]
    public async Task Defaults_are_inherited_and_a_single_stored_field_can_be_reset_without_writing_defaults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db);

        var defaults = await service.GetEffectiveAsync();
        defaults.ApplicationName.Should().Be(new BrandingField("NetRatel", BrandingValueSource.Default, false));
        defaults.Tagline.Should().Be(new BrandingField("Automation Platform", BrandingValueSource.Default, false));

        var updated = await service.UpdateAsync(new([new("applicationName", "Northwind Operations", false)]), "principal-admin");
        updated.ApplicationName.Should().Be(new BrandingField("Northwind Operations", BrandingValueSource.Administrator, false));
        updated.Tagline.Should().Be(new BrandingField("Automation Platform", BrandingValueSource.Default, false));
        var stored = await db.DeploymentBrandingOverrides.SingleAsync();
        stored.ApplicationName.Should().Be("Northwind Operations");
        stored.Tagline.Should().BeNull();

        var reset = await service.UpdateAsync(new([new("applicationName", null, true)]), "principal-admin");
        reset.ApplicationName.Should().Be(new BrandingField("NetRatel", BrandingValueSource.Default, false));
        (await db.DeploymentBrandingOverrides.SingleAsync()).ApplicationName.Should().BeNull();
    }

    [Fact]
    public async Task Deployment_configuration_wins_per_field_and_is_not_editable_by_an_administrator()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db, new DeploymentBrandingOptions { ApplicationName = "Deployment name" });

        var effective = await service.GetEffectiveAsync();
        effective.ApplicationName.Should().Be(new BrandingField("Deployment name", BrandingValueSource.Deployment, true));
        effective.Tagline.Source.Should().Be(BrandingValueSource.Default);

        await service.Invoking(value => value.UpdateAsync(new([new("applicationName", "Ignored", false)]), "principal-admin"))
            .Should().ThrowAsync<BrandingLockedException>();
        (await db.DeploymentBrandingOverrides.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Assets_are_content_validated_versioned_and_never_exposed_as_arbitrary_urls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db);

        await service.Invoking(value => value.UploadAssetAsync(new("logo-light", "image/svg+xml", "<svg/>"u8.ToArray()), "principal-admin"))
            .Should().ThrowAsync<BrandingValidationException>();

        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), 64);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), 64);
        var uploaded = await service.UploadAssetAsync(new("logo-light", "image/png", png), "principal-admin");

        uploaded.Url.Should().StartWith($"/api/v2/branding/assets/{uploaded.AssetId}?v=");
        (await service.GetEffectiveAsync()).LogoLightUrl.Should().Match<BrandingField>(value =>
            value.Source == BrandingValueSource.Administrator && value.Value == uploaded.Url);
        (await service.FindAssetAsync(uploaded.AssetId)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://support.example.test")]
    [InlineData("/help")]
    public void Deployment_urls_accept_only_safe_presentation_locations(string value)
    {
        var validator = new DeploymentBrandingOptionsValidator();
        validator.Validate(null, new DeploymentBrandingOptions { SupportUrl = value }).Succeeded.Should().BeTrue();
        validator.Validate(null, new DeploymentBrandingOptions { SupportUrl = "http://insecure.example.test" }).Succeeded.Should().BeFalse();
        validator.Validate(null, new DeploymentBrandingOptions { SupportUrl = "//untrusted.example.test" }).Succeeded.Should().BeFalse();
    }

    private static DeploymentBrandingService CreateService(NetRatelIdentityDbContext db, DeploymentBrandingOptions? options = null) =>
        new(db, Options.Create(options ?? new DeploymentBrandingOptions()));

    private static NetRatelIdentityDbContext CreateDb(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseSqlite(connection).Options);
}
