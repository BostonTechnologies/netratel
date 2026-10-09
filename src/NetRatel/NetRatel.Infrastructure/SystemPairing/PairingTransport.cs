using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Infrastructure.SystemPairing.Network;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.SystemPairing;

public sealed class PairingTransport(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Origin(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) throw new PairingException(422, "address-invalid", "Enter the other installation address.");
        value = value.Trim().TrimEnd('/');
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || value.Any(char.IsControl))
            throw new PairingException(422, "address-invalid", "Enter a DNS name or IP address with an optional port and HTTP or HTTPS scheme.");
        try { PairingEndpointPolicy.Validate(uri, "Address", true); }
        catch (ArgumentException error) { throw new PairingException(422, "address-blocked", error.Message); }
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public async Task<PairingMetadata> DiscoverAsync(string address, string expectedProduct, CancellationToken ct)
    {
        var origin = Origin(address);
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var proof = await SendAsync<PairingMetadataProof>(origin, HttpMethod.Get, "/metadata", null, null, null, ct, nonce);
        VerifyProof(proof, nonce, null);
        var metadata = proof.Metadata;
        ValidateMetadata(metadata, expectedProduct);
        var api = Origin(metadata.ApiOrigin);
        var verified = await SendAsync<PairingMetadataProof>(api, HttpMethod.Get, "/metadata", null, null, null, ct, nonce);
        VerifyProof(verified, nonce, metadata.SigningPublicKey);
        if (JsonSerializer.Serialize(metadata, Json) != JsonSerializer.Serialize(verified.Metadata, Json))
            throw new PairingException(422, "peer-origin-mismatch", "The Web and API addresses identify different installations.");
        if (origin != api && origin != Origin(metadata.WebOrigin))
        {
            var canonical = await SendAsync<PairingMetadataProof>(Origin(metadata.WebOrigin), HttpMethod.Get, "/metadata", null, null, null, ct, nonce);
            VerifyProof(canonical, nonce, metadata.SigningPublicKey);
            if (JsonSerializer.Serialize(canonical.Metadata, Json) != JsonSerializer.Serialize(metadata, Json))
                throw new PairingException(422, "peer-alias-unverified", "The entered address is not a verified alias of this installation.");
        }
        return metadata;
    }
    public static void VerifyProof(PairingMetadataProof proof, string nonce, string? pinnedKey)
    {
        try
        {
            if (proof?.Metadata is null || proof.Nonce != nonce || proof.Metadata.SigningPublicKey is not { Length: > 0 and <= 8192 } || proof.Signature is not { Length: > 0 and <= 8192 } || pinnedKey is not null && proof.Metadata.SigningPublicKey != pinnedKey) throw new CryptographicException();
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(proof.Metadata.SigningPublicKey), out _);
            if (rsa.KeySize < 2048 || !rsa.VerifyData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proof.Metadata, Json) + ":" + nonce), Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        { throw new PairingException(422, "peer-proof-invalid", "The API address could not prove this installation's identity."); }
    }
    public static void ValidateMetadata(PairingMetadata peer, string product)
    {
        if (peer is null || peer.Contract != PairingProtocol.Contract || peer.Product != product || !Guid.TryParseExact(peer.InstallationId, "D", out var id) || id == Guid.Empty || id.ToString("D") != peer.InstallationId || peer.Name is not { Length: > 0 and <= 128 } || peer.SigningPublicKey is not { Length: > 0 and <= 8192 })
            throw new PairingException(422, "peer-incompatible", "This address does not identify a compatible installation.");
        _ = Origin(peer.ApiOrigin); _ = Origin(peer.WebOrigin);
        if (product == "netratel" && !Guid.TryParseExact(peer.ProducerInstanceId, "D", out _))
            throw new PairingException(422, "producer-unavailable", "The NetRatel installation has no persistent Flow producer identity.");
    }
    public async Task<T> SendAsync<T>(string origin, HttpMethod method, string path, object? body,
        string? secret, string? localInstallationId, CancellationToken ct, string? nonce = null, string? callerSecretHash = null)
    {
        if (!path.StartsWith('/') || path.Contains('?') || path.Contains('#') || path.Contains("..", StringComparison.Ordinal)) throw new InvalidOperationException("fixed-pairing-route-required");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(method, Origin(origin) + PairingProtocol.Route + path);
        if (nonce is not null) request.Headers.Add("X-Pairing-Nonce", nonce);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (secret is not null) { request.Headers.Authorization = new("Pairing", secret); request.Headers.Add("X-Pairing-Peer", localInstallationId); request.Headers.Add("X-Pairing-Caller", callerSecretHash ?? throw new InvalidOperationException("pairing-caller-generation-required")); }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.Content.Headers.ContentLength > 65536) throw new PairingException(502, "peer-response-too-large", "The peer returned an oversized response.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(bytes, deadline.Token)) != 0)
        { if (buffer.Length + read > 65536) throw new PairingException(502, "peer-response-too-large", "The peer returned an oversized response."); await buffer.WriteAsync(bytes.AsMemory(0, read), deadline.Token); }
        if (!response.IsSuccessStatusCode)
        {
            string? message = null; string? code = null;
            try { using var doc = JsonDocument.Parse(buffer.ToArray()); if (doc.RootElement.TryGetProperty("message", out var item)) message = item.GetString(); if (doc.RootElement.TryGetProperty("code", out item)) code = item.GetString(); } catch (JsonException) { }
            if (code is not { Length: > 0 and <= 80 } || code.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_')) code = "peer-rejected";
            var bodySecret = body switch { PairingSaveRequest save => save.Credential?.ClientSecret, PairingExchangeRequest exchange => exchange.InboundSecret, _ => null };
            var codeSecret = body is PairingExchangeRequest codeExchange ? codeExchange.Code : null;
            var sensitive = new[] { secret, bodySecret, codeSecret, codeSecret?.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant() }.Where(x => !string.IsNullOrEmpty(x)).ToArray();
            if (sensitive.Any(x => code.Contains(x!, StringComparison.Ordinal))) code = "peer-rejected";
            if (message is not { Length: > 0 and <= 1024 } || sensitive.Any(x => message.Contains(x!, StringComparison.Ordinal))) message = $"The peer rejected this operation (HTTP {(int)response.StatusCode}).";
            throw new PairingException((int)response.StatusCode, code, message);
        }
        if (typeof(T) == typeof(bool)) return (T)(object)true;
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(buffer.ToArray(), Json) ?? throw new JsonException();
            if (parsed is PairingDirectory directory && (directory.Tenants is null || directory.Customers is null || directory.Tenants.Length > 10000 || directory.Customers.Length > 10000 || directory.Tenants.Concat(directory.Customers).Any(x => x is null || x.Id is not { Length: > 0 and <= 128 } || x.Name is not { Length: > 0 and <= 256 }))) throw new JsonException();
            if (parsed is PairingTestResult test && test.Message is not { Length: > 0 and <= 1024 }) throw new JsonException();
            if (parsed is PairingSaveResponse saved && saved.Mapping is null) throw new JsonException();
            return parsed;
        }
        catch (JsonException) { throw new PairingException(502, "peer-response-invalid", "The peer returned an invalid pairing response."); }
    }
    public async Task<string> TokenAsync(PairingMetadata peer, PairingBusinessCredential credential, string scope, CancellationToken ct)
    {
        if (credential.TokenEndpoint != Origin(peer.ApiOrigin) + "/connect/token" || !credential.Scopes.Contains(scope, StringComparer.Ordinal))
            throw new PairingException(403, "business-credential-mismatch", "The selected connection does not authorize this operation.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, credential.TokenEndpoint)
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = credential.ClientId, ["client_secret"] = credential.ClientSecret, ["scope"] = scope }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new PairingException((int)response.StatusCode, "business-access-rejected", $"The peer rejected the selected connection credential (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(bytes, deadline.Token)) != 0)
        { if (buffer.Length + read > 32768) throw new PairingException(502, "token-response-invalid", "The peer returned an oversized token response."); await buffer.WriteAsync(bytes.AsMemory(0, read), deadline.Token); }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        if (!doc.RootElement.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 and <= 16384 } value) throw new PairingException(502, "token-response-invalid", "The peer did not return a usable scoped access token.");
        return value;
    }
    public async Task<int> PostBusinessStatusAsync(string apiOrigin, string path, object body, string bearer, string correlationId, CancellationToken ct)
    {
        if (path != "/api/v1/orchestration/provider/callback") throw new InvalidOperationException("fixed-callback-route-required");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, Origin(apiOrigin) + path) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Authorization = new("Bearer", bearer); request.Headers.Add("X-Correlation-Id", correlationId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        return (int)response.StatusCode;
    }

}
