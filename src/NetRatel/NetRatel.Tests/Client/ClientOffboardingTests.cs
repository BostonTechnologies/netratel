using System.Text.Json;
using System.Xml.Linq;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Offboarding;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientOffboardingTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void ExecutableOnlyHelper_RequiresSelfContainedSingleFileManifest(bool selfContained, bool singleFile, bool expected)
    {
        using var manifest = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schema = "netratel.client.manifest.v1", product = "NetRatel.Client", runtimeId = "linux-x64",
            executable = "NetRatel.Client", selfContained, singleFile
        }));
        Assert.Equal(expected, ClientOffboardingCoordinator.IsStandaloneManifest(manifest.RootElement, "linux-x64", "NetRatel.Client"));
        Assert.False(ClientOffboardingCoordinator.IsStandaloneManifest(manifest.RootElement, "win-x64", "NetRatel.Client.exe"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schema\":17}")]
    public void MissingOrMalformedManifest_DoesNotAdvertiseUninstall(string json)
    {
        using var manifest = JsonDocument.Parse(json);
        Assert.False(ClientOffboardingCoordinator.IsStandaloneManifest(manifest.RootElement, "linux-x64", "NetRatel.Client"));
    }

    [Theory]
    [InlineData("ExecStart=\"/opt/netratel/client/updater/netratel-update.sh\"", true)]
    [InlineData("ExecStart=/opt/netratel/client/updater/netratel-update.sh", true)]
    [InlineData("ExecStart=/other/client/updater/netratel-update.sh", false)]
    [InlineData("ExecStart=/opt/netratel/client/updater/netratel-update.sh --other", false)]
    [InlineData("ExecStart=/opt/netratel/client/updater/netratel-update.sh\nExecStart=/other/command", false)]
    public void LinuxUpdaterOwnership_AcceptsGeneratedQuoting_AndRejectsOtherCommands(string command, bool expected)
    {
        Assert.Equal(expected, ClientOffboardingCoordinator.IsOwnedLinuxUpdaterUnit("[Service]\n" + command + "\n", "/opt/netratel/client"));
    }

    [Fact]
    public void DuplicateOperation_MayRefreshStartDeadline_ButCannotChangeIdentityOrReceiptCredential()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new GatewayPresenceSession(7, Guid.NewGuid(), 4, Guid.NewGuid());
        var stored = Request(session.AgentId, now);
        var dispatch = Dispatch(stored, now);
        dispatch.StartDeadlineUtc = Timestamp.FromDateTimeOffset(now.AddMinutes(5));
        dispatch.ExpectedKeyFingerprint = stored.ExpectedKeyFingerprint.ToLowerInvariant();
        Assert.True(ClientOffboardingCoordinator.IsValidRequest(session, dispatch, now));
        Assert.True(ClientOffboardingCoordinator.MatchesOperation(stored, session, dispatch));
        Assert.False(ClientOffboardingCoordinator.MatchesOperation(stored, session with { TenantId = 8 }, dispatch));
        Assert.False(ClientOffboardingCoordinator.MatchesOperation(stored, session with { AgentId = Guid.NewGuid() }, dispatch));
        dispatch.CompletionToken = new string('B', 64);
        Assert.False(ClientOffboardingCoordinator.MatchesOperation(stored, session, dispatch));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(361)]
    public void StaleOrUnboundedStartAuthorization_IsRejected(int seconds)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new GatewayPresenceSession(7, Guid.NewGuid(), 4, Guid.NewGuid());
        var dispatch = Dispatch(Request(session.AgentId, now), now);
        dispatch.StartDeadlineUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(seconds));
        Assert.False(ClientOffboardingCoordinator.IsValidRequest(session, dispatch, now));
    }

    [Fact]
    public async Task CompletedRemoval_IsPersistedBeforeDelivery_AndIsNeverRepeatedOnResume()
    {
        var request = Request(Guid.NewGuid(), DateTimeOffset.UtcNow);
        OffboardingResult? persisted = null;
        var calls = new List<string>();
        var result = await ClientOffboardingHelper.EnsureResultAsync(request, null,
            (_, _) => { calls.Add("remove"); return Task.FromResult(new OffboardingResult(true, true, string.Empty)); },
            (value, _) => { calls.Add("persist"); persisted = value; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal(["remove", "persist"], calls);
        Assert.Same(result, persisted);
        var resumed = await ClientOffboardingHelper.EnsureResultAsync(request, persisted,
            (_, _) => throw new InvalidOperationException("Removal must not run again."),
            (_, _) => throw new InvalidOperationException("A terminal result must not change."), TestContext.Current.CancellationToken);
        var receipt = ClientOffboardingHelper.CreateReceipt(request, resumed);
        Assert.True(receipt.Success);
        Assert.Equal(request.OperationId.ToString("D"), receipt.OperationId);
        Assert.Equal(request.CompletionToken, receipt.CompletionToken);
    }

    [Fact]
    public async Task FailedResultPersistence_CannotYieldAReceiptReadyResult()
    {
        var request = Request(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<IOException>(() => ClientOffboardingHelper.EnsureResultAsync(request, null,
            (_, _) => Task.FromResult(new OffboardingResult(true, true, string.Empty)),
            (_, _) => throw new IOException("Fixture disk failure."), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false, "uninstall_native_removal_failed")]
    [InlineData(true, false, "uninstall_files_remain")]
    [InlineData(true, true, "uninstall_verification_failed")]
    public async Task FailedOrPartialRemoval_RemainsFailureAcrossHelperRestarts(bool serviceRemoved, bool filesRemoved, string failure)
    {
        var request = Request(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var persisted = new OffboardingResult(serviceRemoved, filesRemoved, failure);
        var resumed = await ClientOffboardingHelper.EnsureResultAsync(request, persisted,
            (_, _) => throw new InvalidOperationException("Terminal failure is retained."),
            (_, _) => throw new InvalidOperationException("Terminal failure is retained."), TestContext.Current.CancellationToken);
        var receipt = ClientOffboardingHelper.CreateReceipt(request, resumed);
        Assert.False(receipt.Success);
        Assert.Equal(serviceRemoved, receipt.ServiceRemoved);
        Assert.Equal(filesRemoved, receipt.FilesRemoved);
        Assert.Equal(failure, receipt.FailureCode);
    }

    [Fact]
    public void NativeTaskCommand_CarriesOnlyOperationId_AndNoReceiptSecret()
    {
        var request = Request(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var xml = OffboardingPlatformScripts.WindowsTaskXml(request);
        var task = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var arguments = Assert.Single(task.Descendants(ns + "Arguments")).Value;
        Assert.Contains(request.OperationId.ToString("N"), arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(request.CompletionToken, xml, StringComparison.Ordinal);
        Assert.DoesNotContain(request.GatewayEndpoint, xml, StringComparison.Ordinal);
        Assert.Equal("S-1-5-18", Assert.Single(task.Descendants(ns + "UserId")).Value);
    }

    [Fact]
    public void ControlStream_RejectsReplayAndOtherPresenceOwners()
    {
        var options = new GatewayClientOptions { Endpoint = "https://gateway.example.test" };
        var client = new AgentControlGatewayClient(options, _ => { });
        var session = new GatewayPresenceSession(7, Guid.NewGuid(), 4, Guid.NewGuid());
        var frame = new GatewayControlFrame
        {
            ProtocolVersion = options.ProtocolVersion, TenantId = session.TenantId, ClientId = session.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch, ConnectionId = session.ConnectionId.ToString("D"), Sequence = 3
        };
        client.ValidateRequest(frame, session, 2);
        Assert.Equal(StatusCode.DataLoss, Assert.Throws<RpcException>(() => client.ValidateRequest(frame, session, 3)).StatusCode);
        Assert.Equal(StatusCode.DataLoss, Assert.Throws<RpcException>(() => client.ValidateRequest(frame, session, 4)).StatusCode);
        Assert.Equal(StatusCode.DataLoss, Assert.Throws<RpcException>(() => client.ValidateRequest(frame, session with { TenantId = 8 }, 0)).StatusCode);
        Assert.Equal(StatusCode.DataLoss, Assert.Throws<RpcException>(() => client.ValidateRequest(frame, session with { ConnectionEpoch = 5 }, 0)).StatusCode);
    }

    private static OffboardingRequest Request(Guid agentId, DateTimeOffset now) => new(Guid.NewGuid(), 7, agentId,
        new string('A', 64), "https://gateway.example.test", now.AddMinutes(5), now.AddHours(24), new string('C', 64),
        "/opt/netratel/client", "/opt/netratel/client/versions/test/NetRatel.Client", new string('D', 64),
        "/var/lib/netratel/agent.dat", "/var/lib/netratel/update", "/etc/systemd/system/netratel-client.service", null, null);

    private static UninstallRequest Dispatch(OffboardingRequest request, DateTimeOffset now) => new()
    {
        OperationId = request.OperationId.ToString("D"), CompletionToken = request.CompletionToken,
        ExpectedKeyFingerprint = request.ExpectedKeyFingerprint, StartDeadlineUtc = Timestamp.FromDateTimeOffset(now.AddMinutes(5))
    };
}
