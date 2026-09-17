using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Infrastructure.Auth;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentEnrollmentServiceTests
{
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

        var result = await service.EnrollAsync("enr-test", CancellationToken.None);

        result.AgentId.Should().Be(EnrollmentCaptureHandler.AgentId.ToString());
        handler.Path.Should().Be("/api/v1/agents/enroll");
        handler.Signature.Should().NotBeNullOrWhiteSpace();
        handler.Nonce.Should().NotBeNullOrWhiteSpace();
        handler.Timestamp.Should().NotBeNull();
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        var scopes = root.GetProperty("requestedScopes").EnumerateArray().Select(item => item.GetString()!).ToArray();
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

    private sealed class StaticDeviceKeyStore(AgentDeviceKeyMaterial material) : IAgentDeviceKeyStore
    {
        public Task<AgentDeviceKeyMaterial> GetOrCreateAsync(CancellationToken ct) => Task.FromResult(material);

        public Task<AgentDeviceKeyMaterial?> LoadAsync(CancellationToken ct) => Task.FromResult<AgentDeviceKeyMaterial?>(material);
    }

    private sealed class EnrollmentCaptureHandler : HttpMessageHandler
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
                    JsonSerializer.Serialize(new { agentId = AgentId, refreshToken = "refresh", expiresInDays = 7 }),
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
