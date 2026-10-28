using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Shared.Contracts.ComputerUse;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentComputerUseDispatcherTests
{
    [Fact]
    public async Task InFlightActionDoesNotBlockCancellationOrLeaseRevocation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingAction = new TaskCompletionSource<ComputerUseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = Channel.CreateUnbounded<AgentControlFrame>();
        var revocations = 0;
        var interrupted = false;
        var command = Command();
        await using (var dispatcher = new AgentComputerUseDispatcher(async (request, token) =>
        {
            started.SetResult();
            return await pendingAction.Task.WaitAsync(token);
        }, _ => { revocations++; return Task.CompletedTask; },
        (response, token) => responses.Writer.WriteAsync(response, token).AsTask(), () => interrupted = true, default))
        {
            dispatcher.Dispatch(Dispatch(command));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Cancel(new ComputerUseCancellation { OperationId = command.OperationId.ToString("D") });
            var response = await responses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var result = JsonSerializer.Deserialize<ComputerUseResult>(response.ComputerUseCompletion.ResultJson.Span, ComputerUseContract.JsonOptions)!;
            Assert.Equal(ComputerUseStatuses.Cancelled, result.Status);
            Assert.True(result.MayHaveApplied);
        }
        Assert.Equal(1, revocations);
        Assert.False(interrupted);
    }

    [Fact]
    public async Task WrongTargetProviderResponseIsNeverReportedAsSuccess()
    {
        var responses = Channel.CreateUnbounded<AgentControlFrame>();
        var command = Command();
        await using var dispatcher = new AgentComputerUseDispatcher((request, token) => Task.FromResult(
            new ComputerUseResult(ComputerUseContract.Version, request.OperationId, request.SessionId, ComputerUseStatuses.Verified,
                Observation: new("fresh", DateTimeOffset.UtcNow, request.Target with { ProcessId = 999 }, "wrong target"))),
            null, (response, token) => responses.Writer.WriteAsync(response, token).AsTask(), () => { }, default);
        dispatcher.Dispatch(Dispatch(command));
        var response = await responses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var result = JsonSerializer.Deserialize<ComputerUseResult>(response.ComputerUseCompletion.ResultJson.Span, ComputerUseContract.JsonOptions)!;
        Assert.Equal(ComputerUseStatuses.Unknown, result.Status);
        Assert.Equal("invalid_helper_result", result.Code);
        Assert.True(result.MayHaveApplied);
    }

    private static ComputerUseCommand Command() => new(ComputerUseContract.Version, Guid.NewGuid(), Guid.NewGuid(),
        ComputerUseOperations.Act, new(3, "test-user-sid-hash", 123, "window-42"), ComputerUseProviders.NativeWindows,
        true, DateTimeOffset.UtcNow.AddMinutes(1), "fresh-observation", new(ComputerUseActionKinds.Type, Text: "fixture"),
        SessionRevision: 2);

    private static ComputerUseDispatch Dispatch(ComputerUseCommand command) => new()
    {
        OperationId = command.OperationId.ToString("D"),
        CommandJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(command, ComputerUseContract.JsonOptions)),
        DeadlineUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(15))
    };
}
