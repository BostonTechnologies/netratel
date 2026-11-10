using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Auth;
using Xunit;
using NetRatel.Shared.Security;

namespace NetRatel.Tests.Client;

public sealed class AgentEnrollmentServiceTests
{
    // Frozen from the original anonymous JSON writers at ad9eee91 before the
    // metadata migration. Hashes bind exact UTF-8 bytes, including field order,
    // nulls, escaping, nested JSON and requested-scope order.
    [Theory]
    [InlineData(null, "J24E5dkx7qpLvRH38wkt6UJyDgebYDLvP73mF1V/N6c=", "Tpd21fVd3cw6MyYKbQSAQwzQX0Ap3rgsSW9hdte+C2o=", "J2bQeP28/j+rs+4MGXx66mVH/C9qbexsNrV39Vs6mXw=")]
    [InlineData("2001:db8::42", "jhI6ADz2RoPwZ1XzfzXrIhzdHFN80My3HfSRT1od1kw=", "UmeyNHQuvmLhp5rUIJL7hVPrYzy1bmswuV7kJpiwpzY=", "LOqQglLGUtgz7d+aPYsdCanYPba3B4aMQuG0cIQiwDE=")]
    public void EnrollmentJson_PreservesFrozenDeviceRequestAndProofBytes(
        string? reportedAddress, string expectedDeviceHash, string expectedRequestHash, string expectedProofHash)
    {
        var device = new EnrollmentDeviceInfo("Unix", "fixture <os> & \"quoted\" café", "fixture-host",
            reportedAddress, "Arm64", ".NET 10.0.14");
        var scopes = new[] { "netratel:connect", "scope:β", "scope:<>&" };
        const string enrollmentCode = "  enr-<one>&é  ";
        const string publicKey = "fixture+/public==";
        const string algorithm = "ecdsa-p256";
        var deviceJson = JsonSerializer.Serialize(device, AgentEnrollmentJsonContext.Default.EnrollmentDeviceInfo);
        var body = JsonSerializer.Serialize(
            new EnrollmentRequest(enrollmentCode.Trim(), publicKey, algorithm, scopes, deviceJson),
            AgentEnrollmentJsonContext.Default.EnrollmentRequest);

        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(deviceJson))).Should().Be(expectedDeviceHash);
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))).Should().Be(expectedRequestHash);
        PopSignatureService.ComputeEnrollmentBodyHash(enrollmentCode, publicKey, algorithm, deviceJson, scopes)
            .Should().Be(expectedProofHash);
    }

    [Fact]
    public async Task EnrollAsync_SignsStableDeviceIdentityProof()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var material = new AgentDeviceKeyMaterial(
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            "ecdsa-p256");
        var handler = new EnrollmentCaptureHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-api.example") };
        var service = new AgentEnrollmentService(client, new StaticDeviceKeyStore(material));

        var result = await service.EnrollAsync("  eNr-<one>&é  ", CancellationToken.None);

        result.AgentId.Should().Be(EnrollmentCaptureHandler.AgentId.ToString());
        handler.Path.Should().Be("/api/v1/agents/enroll");
        handler.Signature.Should().NotBeNullOrWhiteSpace();
        handler.Nonce.Should().NotBeNullOrWhiteSpace();
        handler.Timestamp.Should().NotBeNull();
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        root.GetProperty("enrollmentCode").GetString().Should().Be("eNr-<one>&é");
        var scopes = root.GetProperty("requestedScopes").EnumerateArray().Select(item => item.GetString()!).ToArray();
        using var device = JsonDocument.Parse(root.GetProperty("deviceInfoJson").GetString()!);
        var reportedOperatingSystem = device.RootElement.GetProperty("os").GetString();
        if (OperatingSystem.IsLinux()) reportedOperatingSystem.Should().Be("Linux");
        else if (OperatingSystem.IsWindows()) reportedOperatingSystem.Should().Be("Windows");
        else if (OperatingSystem.IsMacOS()) reportedOperatingSystem.Should().Be("macOS");
        var bodyHash = PopSignatureService.ComputeEnrollmentBodyHash(
            root.GetProperty("enrollmentCode").GetString()!,
            root.GetProperty("publicKey").GetString()!,
            root.GetProperty("keyAlgorithm").GetString()!,
            root.GetProperty("deviceInfoJson").GetString(),
            scopes);
        var message = PopSignatureService.BuildSigningMessage(
            "POST",
            handler.Path!,
            handler.Timestamp!.Value,
            handler.Nonce!,
            bodyHash);
        PopSignatureService.VerifySignature(material.Algorithm, material.PublicKey, handler.Signature!, message)
            .Should().BeTrue();
    }

    [Fact]
    public async Task EnrollAsync_RetainsWebResponseDefaultsAndIgnoresNewFields()
    {
        var handler = new EnrollmentCaptureHandler("""
            {"AGENTID":"61427545-a59f-41b1-b96f-152981400b27","RefreshToken":"refresh","ExpiresInDays":"7","futureField":true}
            """);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var material = new AgentDeviceKeyMaterial(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()), "ecdsa-p256");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-api.example") };
        var service = new AgentEnrollmentService(client, new StaticDeviceKeyStore(material));

        var result = await service.EnrollAsync("enr-test", CancellationToken.None);

        result.Should().Be((EnrollmentCaptureHandler.AgentId.ToString(), "refresh"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"agentId":"61427545-a59f-41b1-b96f-152981400b27"}""")]
    [InlineData("""{"agentId":"00000000-0000-0000-0000-000000000000","refreshToken":"refresh"}""")]
    public async Task EnrollAsync_StillRejectsIncompleteResponses(string responseBody)
    {
        var handler = new EnrollmentCaptureHandler(responseBody);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var material = new AgentDeviceKeyMaterial(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()), "ecdsa-p256");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-api.example") };
        var service = new AgentEnrollmentService(client, new StaticDeviceKeyStore(material));

        var attempt = () => service.EnrollAsync("enr-test", CancellationToken.None);

        await attempt.Should().ThrowAsync<AgentClientAuthException>().WithMessage("Enrollment response was invalid.");
    }

    private sealed class StaticDeviceKeyStore(AgentDeviceKeyMaterial material) : IAgentDeviceKeyStore
    {
        public Task<AgentDeviceKeyMaterial> GetOrCreateAsync(CancellationToken ct) => Task.FromResult(material);

        public Task<AgentDeviceKeyMaterial?> LoadAsync(CancellationToken ct) => Task.FromResult<AgentDeviceKeyMaterial?>(material);
    }

    private sealed class EnrollmentCaptureHandler(string? responseBody = null) : HttpMessageHandler
    {
        public static readonly Guid AgentId = Guid.Parse("61427545-a59f-41b1-b96f-152981400b27");

        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? Signature { get; private set; }
        public string? Nonce { get; private set; }
        public DateTimeOffset? Timestamp { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Signature = request.Headers.GetValues("X-NetRatel-Signature").Single();
            Nonce = request.Headers.GetValues("X-NetRatel-Nonce").Single();
            Timestamp = DateTimeOffset.Parse(request.Headers.GetValues("X-NetRatel-Timestamp").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody ?? """{"agentId":"61427545-a59f-41b1-b96f-152981400b27","refreshToken":"refresh","expiresInDays":7}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
