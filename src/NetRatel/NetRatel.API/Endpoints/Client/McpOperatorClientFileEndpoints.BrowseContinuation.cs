using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;

namespace NetRatel.API.Endpoints.Client;

public static partial class McpOperatorClientFileEndpoints
{
    private const string BrowseCursorPurpose = "NetRatel.Mcp.Operator.Files.Browse.v1";
    private const int MaximumBrowseEntries = 4_096;
    private const int MaximumBrowseCursorLength = 1_024;
    private const int BrowseCursorPayloadLength = 1 + sizeof(long) + sizeof(int) + 3 * 32;
    private static readonly TimeSpan BrowseCursorLifetime = TimeSpan.FromMinutes(5);

    // The authenticated cursor contains only hashes and an offset. Every page
    // re-enumerates the admitted directory through the native gateway. No file
    // bytes, directory snapshot, or authority are retained by this cursor.
    private sealed record BrowseContinuation(int Offset, DateTimeOffset ExpiresAtUtc, byte[] ListingFingerprint);

    private static byte[] BrowseBinding(McpOperatorFileRouteContext context, string path, int pageSize) =>
        BrowseHash(writer =>
        {
            var request = context.Request;
            writer.Write(request.TenantId);
            writer.Write(request.AgentId.ToByteArray());
            writer.Write(path);
            writer.Write(pageSize);
            writer.Write((int)request.Environment);
            writer.Write(request.Principal.Subject);
            writer.Write(request.Principal.ClientId ?? string.Empty);
            writer.Write(request.Principal.AuthorizedParty ?? string.Empty);
            writer.Write(request.ServicePrincipal);
            writer.Write(request.McpResource);
            writer.Write(request.McpInstance);
        });

    private static byte[] BrowsePolicyFingerprint(McpOperatorDecision decision, string path) =>
        BrowseHash(writer =>
        {
            writer.Write(decision.SelectedPolicyVersion ?? 0);
            writer.Write(decision.TargetSetDigest ?? string.Empty);
            writer.Write(decision.MatchingPolicyIds.Count);
            foreach (var id in decision.MatchingPolicyIds.Order())
                writer.Write(id.ToByteArray());
            _ = TryGetReadRootFingerprint(decision, path, out var readRoots);
            writer.Write(readRoots);
        });

    private static byte[] BrowseListingFingerprint(IReadOnlyList<McpOperatorFileEntry> entries) =>
        BrowseHash(writer =>
        {
            writer.Write(entries.Count);
            foreach (var entry in entries)
            {
                writer.Write(entry.Name);
                writer.Write(entry.Path);
                writer.Write(entry.IsDirectory);
                writer.Write(entry.SizeBytes);
                writer.Write(entry.CreatedUtc.HasValue);
                writer.Write(entry.CreatedUtc?.UtcTicks ?? 0);
                writer.Write(entry.LastModifiedUtc.HasValue);
                writer.Write(entry.LastModifiedUtc?.UtcTicks ?? 0);
            }
        });

    private static byte[] BrowseHash(Action<BinaryWriter> write)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new BrowseHashStream(hash);
        using (var writer = new BinaryWriter(sink, Encoding.UTF8, leaveOpen: true))
            write(writer);
        return hash.GetHashAndReset();
    }

    private static string WriteBrowseCursor(IDataProtector protector, byte[] binding, byte[] policy,
        byte[] listing, int offset, DateTimeOffset expiresAtUtc)
    {
        using var payload = new MemoryStream(BrowseCursorPayloadLength);
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(expiresAtUtc.UtcTicks);
            writer.Write(offset);
            writer.Write(binding);
            writer.Write(policy);
            writer.Write(listing);
        }
        return WebEncoders.Base64UrlEncode(protector.Protect(payload.ToArray()));
    }

    private static string? ReadBrowseCursor(string? cursor, IDataProtector protector, byte[] binding,
        byte[] policy, DateTimeOffset now, out BrowseContinuation? continuation)
    {
        continuation = null;
        if (cursor is null)
            return null;
        if (cursor.Length is 0 or > MaximumBrowseCursorLength || cursor.Any(char.IsWhiteSpace))
            return "invalid_file_cursor";
        try
        {
            var payload = protector.Unprotect(WebEncoders.Base64UrlDecode(cursor));
            if (payload.Length != BrowseCursorPayloadLength)
                return "invalid_file_cursor";
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            if (reader.ReadByte() != 1)
                return "invalid_file_cursor";
            var expiresAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var offset = reader.ReadInt32();
            if (offset is <= 0 or >= MaximumBrowseEntries ||
                !CryptographicOperations.FixedTimeEquals(reader.ReadBytes(32), binding))
                return "invalid_file_cursor";
            if (expiresAt <= now)
                return "file_cursor_expired";
            if (!CryptographicOperations.FixedTimeEquals(reader.ReadBytes(32), policy))
                return "file_cursor_stale";
            continuation = new BrowseContinuation(offset, expiresAt, reader.ReadBytes(32));
            return null;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException or IOException)
        {
            return "invalid_file_cursor";
        }
    }

    private static async Task<string?> RecheckBrowseAdmissionAsync(IMcpOperatorRouteAdmission admission,
        IClientPresenceRouter presence, McpOperatorFileRouteContext context, string path,
        byte[] policyFingerprint, CancellationToken cancellationToken)
    {
        if (!await HasCurrentFilePermissionAsync(context, cancellationToken).ConfigureAwait(false))
            return "permission_denied";
        var target = await presence.GetSnapshotAsync(new ClientKey(context.TenantId, context.AgentId), cancellationToken).ConfigureAwait(false);
        var current = await admission.EvaluateAsync(context.Request with
        {
            TargetOnline = target.Status == ClientPresenceStatus.Online,
            CapabilityAvailable = target.Capabilities.Contains("file-gateway", StringComparer.OrdinalIgnoreCase)
        }, cancellationToken).ConfigureAwait(false);
        if (!current.Decision.IsAllowed)
            return current.Decision.FailureCode ?? "target_policy_missing";
        if (!TryCreateReadPolicy(current.Decision, path, out _))
            return "file_policy_roots_missing";
        return CryptographicOperations.FixedTimeEquals(policyFingerprint, BrowsePolicyFingerprint(current.Decision, path))
            ? null
            : "file_cursor_stale";
    }

    private static IResult BrowseAdmissionFailure(string code, McpOperatorFileRouteContext context) =>
        code == "file_cursor_stale" ? GatewayFailure(code, context) : Failure(code, context);

    // Incremental hashing bounds memory independently of the native listing's
    // path lengths and uses explicit binary fields, including under trimming.
    private sealed class BrowseHashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
