using FluentAssertions;
using Google.Protobuf;
using NetRatel.API.Gateway;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentFileGatewaySessionRegistryTests
{
    [Fact]
    public async Task ListAsync_RoutesA_FencedPagedRequest_AndReturnsEntries()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2);

        var listing = registry.ListAsync(client, "/var/log", 32, CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();
        dispatch.Dispatch.Operation.Should().Be("list");
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TryAddPage(client, new FileListPage
        {
            RequestId = dispatch.Dispatch.RequestId,
            AttemptId = dispatch.Dispatch.AttemptId,
            IsLastPage = true,
            Entries = { new FileEntry { Name = "messages", FullPath = "/var/log/messages", SizeBytes = 42 } }
        }).Should().BeTrue();
        registry.TryComplete(client, new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();

        var entries = await listing;
        entries.Should().ContainSingle(entry => entry.FullPath == "/var/log/messages" && entry.SizeBytes == 42);
    }

    [Fact]
    public async Task ReadAsync_RejectsChunkWithInvalidHash()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2);

        var read = registry.ReadAsync(client, "/var/log/messages", CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        var operation = await read;

        (await registry.TryAddReadChunkAsync(client, new FileTransferChunk
        {
            RequestId = dispatch.Dispatch.RequestId,
            AttemptId = dispatch.Dispatch.AttemptId,
            ChunkIndex = 0,
            Content = ByteString.CopyFromUtf8("tampered"),
            Sha256 = "not-a-sha256"
        }, CancellationToken.None)).Should().BeFalse();

        var completion = () => operation.Completion;
        await completion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListAsync_PreservesTheTypedRemoteFailureCode()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2);

        var listing = registry.ListAsync(client, "/missing", 32, CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TryFail(client, new FileRequestFailed
        {
            RequestId = dispatch.Dispatch.RequestId,
            AttemptId = dispatch.Dispatch.AttemptId,
            Code = "directory_not_found"
        }).Should().BeTrue();

        var assertion = () => listing;
        var exception = await assertion.Should().ThrowAsync<AgentFileGatewayOperationException>();
        exception.Which.Code.Should().Be("directory_not_found");
    }

    [Fact]
    public async Task ReadAsync_GrantsInitialAndReturnedByteCredits()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2);

        var read = registry.ReadAsync(client, "/var/log/messages", CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        var initialCredit = await registration.Reader.ReadAsync();
        initialCredit.Credit.AvailableBytes.Should().Be(FileTransferFrameBudget.PayloadBytes);
        var operation = await read;
        var content = ByteString.CopyFromUtf8("gateway-read");

        (await registry.TryAddReadChunkAsync(client, new FileTransferChunk
        {
            RequestId = dispatch.Dispatch.RequestId,
            AttemptId = dispatch.Dispatch.AttemptId,
            ChunkIndex = 0,
            Content = content,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Span)).ToLowerInvariant()
        }, CancellationToken.None)).Should().BeTrue();

        var returnedCredit = await registration.Reader.ReadAsync();
        returnedCredit.Credit.AvailableBytes.Should().Be((uint)content.Length);
        (await operation.Chunks.ReadAsync()).ToArray().Should().Equal(content.ToByteArray());
    }

    [Fact]
    public async Task PolicyRootConstrainedList_RequiresTheAgentCapability_AndCarriesOnlyTheApprovedRoot()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(
            client,
            connectionId,
            2,
            new HashSet<string>(["file-policy-roots-v1"], StringComparer.Ordinal));

        var listing = registry.ListAsync(
            client,
            "/var/log/netratel",
            32,
            new GatewayFileAccessPolicy(["/var/log"]),
            CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();

        dispatch.Dispatch.AllowedRoots.Should().Equal("/var/log");
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TryAddPage(client, new FileListPage
        {
            RequestId = dispatch.Dispatch.RequestId,
            AttemptId = dispatch.Dispatch.AttemptId,
            IsLastPage = true
        }).Should().BeTrue();
        registry.TryComplete(client, new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();

        (await listing).Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyRootConstrainedRead_FailsClosedWhenTheAgentDidNotAdvertiseTheCapability()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2);

        var action = () => registry.ReadAsync(
            client,
            "/var/log/netratel/agent.log",
            new GatewayFileAccessPolicy(["/var/log"]),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentFileGatewayOperationException>();
        exception.Which.Code.Should().Be("capability_unavailable");
        registration.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task PolicyRootConstrainedStat_RequiresTheAdvertisedCapability_AndReturnsOnlyBoundedMetadata()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(
            client,
            connectionId,
            2,
            new HashSet<string>(["file-policy-roots-v1", "file-stat-v1"], StringComparer.Ordinal));

        var stat = registry.StatAsync(
            client,
            "/var/log/netratel/agent.log",
            new GatewayFileAccessPolicy(["/var/log"]),
            CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();

        dispatch.Dispatch.Operation.Should().Be("stat");
        dispatch.Dispatch.AllowedRoots.Should().Equal("/var/log");
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TrySetMetadata(client, new FileMetadata
        {
            RequestId = dispatch.Dispatch.RequestId,
            AttemptId = dispatch.Dispatch.AttemptId,
            FullPath = "/var/log/netratel/agent.log",
            SizeBytes = 42,
            LastModifiedUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)),
            MimeType = "text/plain; charset=utf-8"
        }).Should().BeTrue();
        registry.TryComplete(client, new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();

        var metadata = await stat;
        metadata.Should().BeEquivalentTo(new GatewayFileMetadata(
            "/var/log/netratel/agent.log",
            false,
            42,
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            "text/plain; charset=utf-8"));
    }

    [Fact]
    public async Task PolicyRootConstrainedStat_FailsClosedWhenTheAgentDidNotAdvertiseTheCapability()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2, new HashSet<string>(["file-policy-roots-v1"], StringComparer.Ordinal));

        var action = () => registry.StatAsync(
            client,
            "/var/log/netratel/agent.log",
            new GatewayFileAccessPolicy(["/var/log"]),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentFileGatewayOperationException>();
        exception.Which.Code.Should().Be("capability_unavailable");
        registration.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task PolicyRootConstrainedCreateDirectory_RequiresTheAdvertisedCapability_AndCompletesExactlyOnce()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(
            client,
            connectionId,
            2,
            new HashSet<string>(["file-policy-roots-v1", "file-create-directory-v1"], StringComparer.Ordinal));

        var create = registry.CreateDirectoryAsync(
            client,
            "/var/lib/netratel/exports",
            new GatewayFileAccessPolicy(["/var/lib/netratel"]),
            CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();

        dispatch.Dispatch.Operation.Should().Be("create_directory");
        dispatch.Dispatch.AllowedRoots.Should().Equal("/var/lib/netratel");
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TryComplete(client, new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        await create;
    }

    [Fact]
    public async Task PolicyRootConstrainedCreateDirectory_FailsClosedWhenTheAgentDidNotAdvertiseTheCapability()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2, new HashSet<string>(["file-policy-roots-v1"], StringComparer.Ordinal));

        var action = () => registry.CreateDirectoryAsync(
            client,
            "/var/lib/netratel/exports",
            new GatewayFileAccessPolicy(["/var/lib/netratel"]),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentFileGatewayOperationException>();
        exception.Which.Code.Should().Be("capability_unavailable");
        registration.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task PolicyRootConstrainedDelete_RequiresTheAdvertisedCapability_AndCompletesExactlyOnce()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(
            client,
            connectionId,
            2,
            new HashSet<string>(["file-policy-roots-v1", "file-delete-v1"], StringComparer.Ordinal));

        var delete = registry.DeleteAsync(
            client,
            "/var/lib/netratel/exports/marker.txt",
            new GatewayFileAccessPolicy(["/var/lib/netratel"]),
            CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();

        dispatch.Dispatch.Operation.Should().Be("delete");
        dispatch.Dispatch.AllowedRoots.Should().Equal("/var/lib/netratel");
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TryComplete(client, new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        await delete;
    }

    [Fact]
    public async Task PolicyRootConstrainedDelete_FailsClosedWhenTheAgentDidNotAdvertiseTheCapability()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2, new HashSet<string>(["file-policy-roots-v1"], StringComparer.Ordinal));

        var action = () => registry.DeleteAsync(
            client,
            "/var/lib/netratel/exports/marker.txt",
            new GatewayFileAccessPolicy(["/var/lib/netratel"]),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentFileGatewayOperationException>();
        exception.Which.Code.Should().Be("capability_unavailable");
        registration.Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("copy", "file-copy-v1")]
    [InlineData("move", "file-move-v1")]
    public async Task PolicyRootConstrainedMoveCopy_RequiresTheAdvertisedCapability_AndCarriesIndependentRoots(
        string operationName,
        string capability)
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(
            client,
            connectionId,
            2,
            new HashSet<string>(["file-policy-roots-v1", capability], StringComparer.Ordinal));
        var accessPolicy = new GatewayFileMoveCopyAccessPolicy(["/var/log"], ["/var/lib/netratel"]);

        Task relocation = operationName == "copy"
            ? registry.CopyAsync(client, "/var/log/agent.log", "/var/lib/netratel/agent-copy.log", accessPolicy, CancellationToken.None)
            : registry.MoveAsync(client, "/var/log/agent.log", "/var/lib/netratel/agent-moved.log", accessPolicy, CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();

        dispatch.Dispatch.Operation.Should().Be(operationName);
        dispatch.Dispatch.Path.Should().Be("/var/log/agent.log");
        dispatch.Dispatch.DestinationPath.Should().Be(operationName == "copy" ? "/var/lib/netratel/agent-copy.log" : "/var/lib/netratel/agent-moved.log");
        dispatch.Dispatch.AllowedRoots.Should().BeEmpty();
        dispatch.Dispatch.SourceAllowedRoots.Should().Equal("/var/log");
        dispatch.Dispatch.DestinationAllowedRoots.Should().Equal("/var/lib/netratel");
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TryComplete(client, new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        await relocation;
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task PolicyRootConstrainedMoveCopy_FailsClosedWhenTheAgentDidNotAdvertiseTheOperationCapability(string operationName)
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2, new HashSet<string>(["file-policy-roots-v1"], StringComparer.Ordinal));
        var accessPolicy = new GatewayFileMoveCopyAccessPolicy(["/var/log"], ["/var/lib/netratel"]);

        var action = () => operationName == "copy"
            ? registry.CopyAsync(client, "/var/log/agent.log", "/var/lib/netratel/agent-copy.log", accessPolicy, CancellationToken.None)
            : registry.MoveAsync(client, "/var/log/agent.log", "/var/lib/netratel/agent-moved.log", accessPolicy, CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentFileGatewayOperationException>();
        exception.Which.Code.Should().Be("capability_unavailable");
        registration.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Stat_RejectsMalformedTimestampMetadataBeforeCompletingTheRequest()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connectionId, 2));
        using var registration = registry.Register(client, connectionId, 2, new HashSet<string>(["file-stat-v1"], StringComparer.Ordinal));

        var stat = registry.StatAsync(client, "/var/log/agent.log", CancellationToken.None);
        var dispatch = await registration.Reader.ReadAsync();
        registry.TryAccept(client, new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        registry.TrySetMetadata(client, new FileMetadata
        {
            RequestId = dispatch.Dispatch.RequestId,
            AttemptId = dispatch.Dispatch.AttemptId,
            FullPath = "/var/log/agent.log",
            SizeBytes = 42,
            LastModifiedUtc = new Google.Protobuf.WellKnownTypes.Timestamp { Nanos = 1_000_000_000 },
            MimeType = "text/plain; charset=utf-8"
        }).Should().BeFalse();
        registry.TryComplete(client, new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeFalse();

        var assertion = () => stat;
        var exception = await assertion.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("metadata");
    }

    [Fact]
    public void TransferFrames_ReserveSpaceForProtobufAndGrpcEnvelope()
    {
        var content = ByteString.CopyFrom(new byte[FileTransferFrameBudget.PayloadBytes]);
        var chunk = new FileTransferChunk
        {
            RequestId = Guid.NewGuid().ToString("D"),
            AttemptId = Guid.NewGuid().ToString("D"),
            ChunkIndex = 42,
            Content = content,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Span)).ToLowerInvariant()
        };

        var clientFrame = new AgentFileFrame
        {
            ProtocolVersion = "1.0",
            TenantId = 3,
            ClientId = Guid.NewGuid().ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"),
            ConnectionEpoch = 2,
            Sequence = 1,
            TransferChunk = chunk
        };
        var gatewayFrame = new GatewayFileFrame
        {
            ProtocolVersion = "1.0",
            TenantId = 3,
            ClientId = Guid.NewGuid().ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"),
            ConnectionEpoch = 2,
            Sequence = 1,
            TransferChunk = chunk
        };

        clientFrame.CalculateSize().Should().BeLessThan(16 * 1024);
        gatewayFrame.CalculateSize().Should().BeLessThan(16 * 1024);
    }

    [Fact]
    public async Task Register_ReplacesOnlyThePriorTransport_AndFailsItsPendingRequestDeterministically()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var firstConnection = Guid.NewGuid();
        var replacementConnection = Guid.NewGuid();
        var presence = new SwitchingPresenceRouter(client, firstConnection, 2);
        var registry = new AgentFileGatewaySessionRegistry(presence);
        using var first = registry.Register(client, firstConnection, 2, new HashSet<string>(["file-stat-v1"], StringComparer.Ordinal));

        var pending = registry.ListAsync(client, "/var/log", 32, CancellationToken.None);
        var firstDispatch = await first.Reader.ReadAsync();
        first.TryAccept(new FileRequestAccepted
        {
            RequestId = firstDispatch.Dispatch.RequestId,
            AttemptId = firstDispatch.Dispatch.AttemptId
        }).Should().BeTrue();

        presence.SetCurrentFence(replacementConnection, 3);
        using var replacement = registry.Register(client, replacementConnection, 3, new HashSet<string>(["file-policy-roots-v1"], StringComparer.Ordinal));

        var assertion = () => pending;
        var failure = await assertion.Should().ThrowAsync<AgentFileGatewaySessionUnavailableException>();
        failure.Which.Code.Should().Be("file_gateway_session_replaced");
        first.TryComplete(new FileRequestCompleted
        {
            RequestId = firstDispatch.Dispatch.RequestId,
            AttemptId = firstDispatch.Dispatch.AttemptId
        }).Should().BeFalse("the old transport must not complete work for its replacement");

        var replacementRequest = registry.ListAsync(client, "/var/log", 32, CancellationToken.None);
        var replacementDispatch = await replacement.Reader.ReadAsync();
        replacement.TryAccept(new FileRequestAccepted
        {
            RequestId = replacementDispatch.Dispatch.RequestId,
            AttemptId = replacementDispatch.Dispatch.AttemptId
        }).Should().BeTrue();
        replacement.TryAddPage(new FileListPage
        {
            RequestId = replacementDispatch.Dispatch.RequestId,
            AttemptId = replacementDispatch.Dispatch.AttemptId,
            IsLastPage = true
        }).Should().BeTrue();
        replacement.TryComplete(new FileRequestCompleted
        {
            RequestId = replacementDispatch.Dispatch.RequestId,
            AttemptId = replacementDispatch.Dispatch.AttemptId
        }).Should().BeTrue();
        (await replacementRequest).Should().BeEmpty();
    }

    [Fact]
    public async Task Register_RejectsAnOlderOrAmbiguousFence_AndRetainsTheCurrentTransport()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var currentConnection = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, currentConnection, 5));
        using var current = registry.Register(client, currentConnection, 5, new HashSet<string>(["file-stat-v1"], StringComparer.Ordinal));

        var staleEpoch = () => registry.Register(client, Guid.NewGuid(), 4, new HashSet<string>(StringComparer.Ordinal));
        staleEpoch.Should().Throw<AgentGatewayRegistrationFencedException>();
        var ambiguousFence = () => registry.Register(client, Guid.NewGuid(), 5, new HashSet<string>(StringComparer.Ordinal));
        ambiguousFence.Should().Throw<AgentGatewayRegistrationFencedException>();

        var availability = registry.GetAvailability(client);
        availability.Should().NotBeNull();
        availability!.ConnectionId.Should().Be(currentConnection);
        availability.ConnectionEpoch.Should().Be(5);

        var request = registry.ListAsync(client, "/var/log", 32, CancellationToken.None);
        var dispatch = await current.Reader.ReadAsync();
        current.TryAccept(new FileRequestAccepted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        current.TryAddPage(new FileListPage { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId, IsLastPage = true }).Should().BeTrue();
        current.TryComplete(new FileRequestCompleted { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId }).Should().BeTrue();
        (await request).Should().BeEmpty();
    }

    [Fact]
    public void DisposingAnOldRegistration_DoesNotRemoveTheReplacementTransport()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var firstConnection = Guid.NewGuid();
        var replacementConnection = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, replacementConnection, 3));
        var first = registry.Register(client, firstConnection, 2, new HashSet<string>(StringComparer.Ordinal));
        using var replacement = registry.Register(client, replacementConnection, 3, new HashSet<string>(["file-stat-v1"], StringComparer.Ordinal));

        first.Dispose();

        var availability = registry.GetAvailability(client);
        availability.Should().NotBeNull();
        availability!.ConnectionId.Should().Be(replacementConnection);
        availability.ConnectionEpoch.Should().Be(3);
        availability.NegotiatedCapabilities.Should().Equal("file-stat-v1");
    }

    [Fact]
    public async Task ExactReconnect_RejectsOldCompletionForNewRequest_AndPreservesReplacementAfterDispose()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connection, 5));
        using var old = registry.Register(client, connection, 5);
        using var current = registry.Register(client, connection, 5);
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        old.IsCurrent.Should().BeFalse();
        old.Dispose();
        current.IsCurrent.Should().BeTrue();
        var pending = registry.ListAsync(client, "/var/log", 32, CancellationToken.None);
        var dispatch = (await current.Reader.ReadAsync()).Dispatch;
        var completed = new FileRequestCompleted { RequestId = dispatch.RequestId, AttemptId = dispatch.AttemptId };
        old.TryComplete(completed).Should().BeFalse();
        pending.IsCompleted.Should().BeFalse();
        current.TryAccept(new FileRequestAccepted { RequestId = dispatch.RequestId, AttemptId = dispatch.AttemptId }).Should().BeTrue();
        current.TryAddPage(new FileListPage { RequestId = dispatch.RequestId, AttemptId = dispatch.AttemptId, IsLastPage = true }).Should().BeTrue();
        current.TryComplete(completed).Should().BeTrue();
        (await pending).Should().BeEmpty();
    }

    [Fact]
    public void ProvisionalRegistration_IsInvisibleUntilActivated_AndCannotActivateAfterReplacement()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var registry = new AgentFileGatewaySessionRegistry(new CurrentPresenceRouter(client, connection, 5));
        using var candidate = registry.Register(client, connection, 5, new HashSet<string>(), provisional: true);
        registry.GetAvailability(client).Should().BeNull();
        Action dispatch = () => _ = registry.ListAsync(client, "/var/log", 32, CancellationToken.None);
        dispatch.Should().Throw<AgentFileGatewaySessionUnavailableException>();
        using var replacement = registry.Register(client, connection, 5, new HashSet<string>(), provisional: true);
        candidate.TryActivate().Should().BeFalse();
        replacement.TryActivate().Should().BeTrue();
        registry.GetAvailability(client).Should().NotBeNull();
    }

    private sealed class CurrentPresenceRouter(ClientKey expectedClient, Guid connectionId, long epoch) : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(
                client,
                client == expectedClient ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                epoch,
                connectionId,
                0,
                DateTimeOffset.UtcNow,
                "test",
                Array.Empty<string>(),
                null,
                "akka-dev-canary",
                true));
    }

    private sealed class SwitchingPresenceRouter(ClientKey expectedClient, Guid connectionId, long epoch) : IClientPresenceRouter
    {
        private Guid _connectionId = connectionId;
        private long _epoch = epoch;

        public void SetCurrentFence(Guid connectionId, long epoch)
        {
            _connectionId = connectionId;
            _epoch = epoch;
        }

        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(
                client,
                client == expectedClient ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                Volatile.Read(ref _epoch),
                _connectionId,
                0,
                DateTimeOffset.UtcNow,
                "test",
                Array.Empty<string>(),
                null,
                "akka-dev-canary",
                true));
    }
}
