using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Security;

internal sealed class SecretKeyRing(IConfiguration configuration)
{
    public const string WrapCommand = "--wrap-data-protection-keys";
    private static readonly XNamespace ProtectionXml = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XNamespace EncryptedXml = "http://www.w3.org/2001/04/xmlenc#";
    private readonly IConfiguration _settings = configuration.GetSection("DataProtection");

    public void Configure(IServiceCollection services)
    {
        var directory = KeyDirectory();
        directory.Create();
        var keys = ReadKeys(directory);
        var configured = !string.IsNullOrWhiteSpace(_settings["WrappingCertificatePath"]);
        if (!configured && keys.Any(key => HasEncryptedMaterial(key.Xml)))
            throw new InvalidOperationException("The API key ring contains wrapped keys. Configure its wrapping certificates before starting the API.");
        if (configured && keys.Any(key => !IsWrapped(key.Xml)))
            throw new InvalidOperationException($"Enroll the existing API key ring with {WrapCommand} before enabling certificate protection.");

        var builder = services.AddDataProtection().PersistKeysToFileSystem(directory)
            .SetApplicationName(_settings["ApplicationName"] ?? "NetRatel-Keyring");
        if (!configured)
            return;
        var active = LoadCertificate(_settings["WrappingCertificatePath"], _settings["WrappingCertificatePasswordFile"]);
        var previous = _settings.GetSection("PreviousWrappingCertificates").GetChildren()
            .Select(section => LoadCertificate(section["Path"], section["PasswordFile"])).ToArray();
        builder.ProtectKeysWithCertificate(active).UnprotectKeysWithAnyCertificate(previous);
    }

    public void VerifyRestoredKeys(IServiceProvider services)
    {
        if (string.IsNullOrWhiteSpace(_settings["WrappingCertificatePath"]))
            return;
        try
        {
            var files = ReadKeys(KeyDirectory());
            var keys = services.GetRequiredService<IKeyManager>().GetAllKeys();
            foreach (var key in keys)
            {
                if (key.CreateEncryptor() is null)
                    throw new CryptographicException("A key has no usable encryptor.");
            }
            if (files.Any(file => !keys.Any(key => key.KeyId == (Guid)file.Xml.Attribute("id")!)))
                throw new CryptographicException("The persisted key set is incomplete.");
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException or XmlException)
        {
            throw new InvalidOperationException("The API key ring could not be opened. Restore the original key files and matching wrapping certificates.");
        }
    }

    public async Task RequireRingForProtectedVaultAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        if (ReadKeys(KeyDirectory()).Length != 0)
            return;
        if (await db.Secrets.AsNoTracking().AnyAsync(secret => secret.ValueProtected, ct) ||
            await db.SecretProviderConnections.AsNoTracking().AnyAsync(connection => connection.ProtectedAuthentication != null, ct) ||
            await db.JobParameters.AsNoTracking().AnyAsync(parameter => parameter.ProtectedDefaultValue != null, ct) ||
            await db.JobRuns.AsNoTracking().AnyAsync(run => run.ProtectedExecutionJson != null, ct))
            throw new InvalidOperationException("Existing encrypted data requires its original API key ring. Restore the key files before starting the API.");
    }

    public int WrapExistingKeys()
    {
        var ring = KeyDirectory();
        if (!ring.Exists)
            throw new InvalidOperationException("The API key ring directory does not exist.");
        using var certificate = LoadCertificate(_settings["WrappingCertificatePath"], _settings["WrappingCertificatePasswordFile"]);
        var encryptor = new CertificateXmlEncryptor(certificate, NullLoggerFactory.Instance);
        var pending = ReadKeys(ring).Where(key => !IsWrapped(key.Xml)).ToArray();
        foreach (var key in pending)
        {
            var elements = PlaintextElements(key.Xml);
            if (elements.Length == 0)
                throw new InvalidOperationException("An API key has an unsupported protection format.");
            foreach (var element in elements)
            {
                var encrypted = encryptor.Encrypt(new XElement(element));
                element.ReplaceWith(new XElement(ProtectionXml + "encryptedSecret",
                    new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName!), encrypted.EncryptedElement));
            }
            if (!IsWrapped(key.Xml))
                throw new InvalidOperationException("An existing API key could not be wrapped.");
        }
        foreach (var key in pending)
            StoreAtomically(key);
        return pending.Length;
    }

    private DirectoryInfo KeyDirectory()
    {
        var path = _settings["KeysDirectory"];
        return new(Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? ".keys" : path, AppContext.BaseDirectory));
    }

    private static KeyFile[] ReadKeys(DirectoryInfo directory) => directory.EnumerateFiles("key-*.xml")
        .Select(file =>
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 64 * 1024)
                throw new InvalidOperationException("The API key ring contains an unsupported key file.");
            using var reader = XmlReader.Create(file.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 64 * 1024 });
            var xml = XElement.Load(reader);
            if (xml.Name != "key" || !Guid.TryParse((string?)xml.Attribute("id"), out _))
                throw new InvalidOperationException("The API key ring contains an invalid key descriptor.");
            return new KeyFile(file.FullName, xml);
        }).ToArray();

    private static XElement[] PlaintextElements(XElement key) => key.Descendants()
        .Where(element => (bool?)element.Attribute(ProtectionXml + "requiresEncryption") == true).ToArray();

    private static bool HasEncryptedMaterial(XElement key) => key.Descendants(ProtectionXml + "encryptedSecret").Any();

    private static bool IsWrapped(XElement key) => PlaintextElements(key).Length == 0 &&
        key.Descendants(ProtectionXml + "encryptedSecret").Any(element => element.Element(EncryptedXml + "EncryptedData") is not null);

    private static X509Certificate2 LoadCertificate(string? path, string? passwordFile)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            passwordFile is not null && !Path.IsPathFullyQualified(passwordFile))
            throw new InvalidOperationException("Configure absolute file paths for the API key-ring certificate and its password file.");
        var password = passwordFile is null ? null : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null)
            return certificate;
        certificate.Dispose();
        throw new InvalidOperationException("The API key-ring certificate requires an RSA private key.");
    }

    private static void StoreAtomically(KeyFile key)
    {
        var temporary = key.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                key.Xml.Save(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, key.Path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private sealed record KeyFile(string Path, XElement Xml);
}
