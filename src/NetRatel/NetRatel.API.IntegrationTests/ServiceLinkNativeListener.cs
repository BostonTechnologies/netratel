using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Ephemeral CA and server identity for the complete product's real HTTP/2 Kestrel socket.</summary>
internal sealed class ServiceLinkNativeListener : IDisposable
{
    public int Port { get; } = ServiceLinkHttpProxy.AllocatePort();
    public string Endpoint => $"https://127.0.0.1:{Port}";
    public X509Certificate2 ServerCertificate { get; }
    public string CaPem { get; }
    public string CaSha256 { get; }

    public ServiceLinkNativeListener()
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Disposable service-link fixture CA", caKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var ca = caRequest.CreateSelfSigned(now.AddMinutes(-2), now.AddHours(2));
        CaPem = ca.ExportCertificatePem();
        CaSha256 = Convert.ToHexStringLower(SHA256.HashData(ca.RawData));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, false));
        using var issued = serverRequest.Create(ca, now.AddMinutes(-1), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        ServerCertificate = issued.CopyWithPrivateKey(serverKey);
    }

    public void ConfigureKestrel(KestrelServerOptions options)
    {
        // Program owns REST + h2c listeners through NetRatel_HTTP_PORT/GatewayGrpcPort.
        // Add only this independent real TLS HTTP/2 socket; do not bind either product port twice.
        options.Listen(IPAddress.Loopback, Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(ServerCertificate);
        });
    }

    public void Dispose() => ServerCertificate.Dispose();
}
