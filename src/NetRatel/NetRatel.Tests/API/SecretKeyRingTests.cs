using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Security;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class SecretKeyRingTests
{
    [Fact]
    public void Enrollment_and_certificate_rotation_keep_existing_key_ids_and_ciphertexts()
    {
        using var scratch = new TemporaryDirectory();
        var keys = Path.Combine(scratch.Path, "keys");
        var originalCertificate = CreateCertificate(scratch.Path, "first");
        var nextCertificate = CreateCertificate(scratch.Path, "second");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:KeysDirectory"] = keys,
            ["DataProtection:ApplicationName"] = "Canary API"
        }).Build();
        var ring = new SecretKeyRing(configuration);
        const string purpose = "historical-job-protected-execution";
        string original;
        Guid keyId;
        using (var before = Build(ring))
        {
            original = before.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect("synthetic historical material");
            keyId = Assert.Single(before.GetRequiredService<IKeyManager>().GetAllKeys()).KeyId;
        }

        configuration["DataProtection:WrappingCertificatePath"] = originalCertificate;
        Assert.Throws<InvalidOperationException>(() => ring.Configure(new ServiceCollection()));
        Assert.Equal(1, ring.WrapExistingKeys());
        Assert.Equal(0, ring.WrapExistingKeys());
        using (var enrolled = Build(ring))
        {
            ring.VerifyRestoredKeys(enrolled);
            Assert.Equal(keyId, Assert.Single(enrolled.GetRequiredService<IKeyManager>().GetAllKeys()).KeyId);
            Assert.Equal("synthetic historical material", enrolled.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(original));
        }

        configuration["DataProtection:WrappingCertificatePath"] = nextCertificate;
        using (var missing = Build(ring))
            Assert.Throws<InvalidOperationException>(() => ring.VerifyRestoredKeys(missing));
        configuration["DataProtection:PreviousWrappingCertificates:0:Path"] = originalCertificate;
        using var rotated = Build(ring);
        var manager = rotated.GetRequiredService<IKeyManager>();
        var date = DateTimeOffset.UtcNow;
        manager.CreateNewKey(date, date.AddDays(90));
        ring.VerifyRestoredKeys(rotated);
        Assert.Equal(2, manager.GetAllKeys().Count);
        Assert.Equal("synthetic historical material", rotated.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Unprotect(original));
    }

    private static ServiceProvider Build(SecretKeyRing ring)
    {
        var services = new ServiceCollection();
        ring.Configure(services);
        return services.BuildServiceProvider();
    }

    private static string CreateCertificate(string directory, string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=NetRatel fixture {name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        var destination = Path.Combine(directory, name + ".pfx");
        File.WriteAllBytes(destination, certificate.Export(X509ContentType.Pkcs12));
        return destination;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("netratel-keyring-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
