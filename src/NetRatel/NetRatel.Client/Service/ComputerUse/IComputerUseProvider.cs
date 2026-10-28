using System;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Client.Service.ComputerUse;

/// <summary>A local execution driver; it never decides authorization or chooses a fallback after input.</summary>
internal interface IComputerUseProvider : IAsyncDisposable
{
    Task<ComputerUseResult> ExecuteAsync(ComputerUseCommand command, CancellationToken cancellationToken = default);
}

internal sealed record ComputerUsePipeRequest(ComputerUseCommand Command, ComputerUseOptions Options);
internal static class ComputerUsePipeKinds
{
    internal const string Request = "computer_use_request";
    internal const string Result = "computer_use_result";
    internal const string Cancel = "computer_use_cancel";
    internal const string Revoke = "computer_use_revoke";
    internal const string Revoked = "computer_use_revoked";
}

internal sealed class ComputerUseRefusalException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal static class ComputerUseDiagnostics
{
    // Never log exception messages, UI state, screenshots, key values or typed text.
    internal static void Record(string code, Exception? exception = null) =>
        Logging.LogManager.WriteLog($"[ComputerUse] code={code} exceptionType={exception?.GetType().Name ?? "none"}");
}
