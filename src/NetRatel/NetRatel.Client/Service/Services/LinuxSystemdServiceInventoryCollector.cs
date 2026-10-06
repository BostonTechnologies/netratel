using NetRatel.Shared.Contracts.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Services;

public sealed record SystemdCommandResult(int ExitCode, string Output, bool Truncated = false);

/// <summary>The adapter accepts argument vectors; service names are never interpreted by a shell.</summary>
public interface ISystemdServiceAdapter
{
    Task<SystemdCommandResult> QueryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class LinuxSystemdServiceInventoryCollector(
    ISystemdServiceAdapter? adapter = null,
    TimeProvider? timeProvider = null) : IServiceInventoryCollector
{
    private const int QueryBatchSize = 48;
    private readonly ISystemdServiceAdapter _adapter = adapter ?? new SystemctlServiceAdapter();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private static readonly string[] Properties = ["Id", "Names", "Description", "LoadState", "ActiveState", "SubState", "UnitFileState"];

    public async Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken)
    {
        var observed = _timeProvider.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ClientServicesLimits.CollectionTimeout);
        try
        {
            // Installed units include stopped/unloaded services; loaded units include transient services.
            var installed = await _adapter.QueryAsync(["list-unit-files", "--type=service", "--no-legend", "--no-pager", "--plain"], deadline.Token).ConfigureAwait(false);
            var loaded = await _adapter.QueryAsync(["list-units", "--type=service", "--all", "--no-legend", "--no-pager", "--plain"], deadline.Token).ConfigureAwait(false);
            if (installed.ExitCode != 0 || loaded.ExitCode != 0)
                return Result(ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Error, observed, [], "systemd-query-failed");
            var unitFiles = ParseUnitFiles(installed.Output);
            var names = unitFiles.Keys.Concat(ParseLoadedNames(loaded.Output)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var truncated = installed.Truncated || loaded.Truncated || names.Length > ClientServicesLimits.MaximumServices ||
                HasMalformedListRows(installed.Output, 2) || HasMalformedListRows(loaded.Output, 4);
            var observations = await QueryServicesAsync(names.Take(ClientServicesLimits.MaximumServices).ToArray(), unitFiles, false, observed, deadline.Token).ConfigureAwait(false);
            return Result(ServiceSnapshotKind.Inventory, truncated || observations.Partial ? ServiceCollectionStatus.Partial : ServiceCollectionStatus.Complete,
                observed, observations.Services, truncated ? "inventory-limit" : observations.Partial ? "systemd-query-partial" : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Result(ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Error, observed, [], "collection-timeout"); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result(ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Unsupported, observed, [], "systemd-unavailable");
        }
    }

    public async Task<ServiceCollectionResult> CollectWatchAsync(IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken)
    {
        var observed = _timeProvider.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ClientServicesLimits.CollectionTimeout);
        if (names.Count > ClientServicesLimits.MaximumWatchServices || names.Any(name => !ClientServiceContractValidator.IsValidServiceName(name, ClientServicePlatform.LinuxSystemd)))
            return Result(ServiceSnapshotKind.Watch, ServiceCollectionStatus.Error, observed, [], "invalid-watch-policy", policyRevision);
        try
        {
            var queried = await QueryServicesAsync(names.Distinct(StringComparer.Ordinal).ToArray(), new Dictionary<string, string>(StringComparer.Ordinal), true, observed, deadline.Token).ConfigureAwait(false);
            var evidence = queried.Partial ? queried.Services.Select(service => service.AuthoritativeMissing
                ? service with { State = ClientServiceState.Unknown, AuthoritativeMissing = false } : service).ToArray() : queried.Services;
            return Result(ServiceSnapshotKind.Watch, queried.Partial ? ServiceCollectionStatus.Partial : ServiceCollectionStatus.Complete,
                observed, evidence, queried.Partial ? "systemd-query-partial" : null, policyRevision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Result(ServiceSnapshotKind.Watch, ServiceCollectionStatus.Error, observed, [], "collection-timeout", policyRevision); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result(ServiceSnapshotKind.Watch, ServiceCollectionStatus.Unsupported, observed, [], "systemd-unavailable", policyRevision);
        }
    }

    private async Task<(IReadOnlyList<ClientServiceObservation> Services, bool Partial)> QueryServicesAsync(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, string> unitFiles, bool allowMissing,
        DateTimeOffset observed, CancellationToken cancellationToken)
    {
        var services = new List<ClientServiceObservation>();
        var partial = false;
        foreach (var batch in names.Chunk(QueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = new List<string> { "show", "--no-pager", "--property=" + string.Join(',', Properties), "--" };
            arguments.AddRange(batch);
            var query = await _adapter.QueryAsync(arguments, cancellationToken).ConfigureAwait(false);
            var blocks = ParsePropertyBlocks(query.Output);
            var successful = query.ExitCode == 0 && !query.Truncated;
            partial |= !successful;
            foreach (var name in batch)
            {
                if (!successful || !blocks.TryGetValue(name, out var properties) || !HasStateProperties(properties))
                {
                    partial = true;
                    services.Add(Unknown(name, observed, unitFiles.GetValueOrDefault(name)));
                    continue;
                }
                var load = Value(properties, "LoadState");
                var active = Value(properties, "ActiveState");
                var sub = Value(properties, "SubState");
                var missing = allowMissing && load == "not-found";
                services.Add(new ClientServiceObservation(name, BoundedDisplayName(Value(properties, "Description"), name), ClientServicePlatform.LinuxSystemd,
                    NormalizeState(load, active, sub, missing), Bounded(active + "/" + sub), null, Bounded(load), Bounded(active), Bounded(sub),
                    Bounded(Value(properties, "UnitFileState"), unitFiles.GetValueOrDefault(name)), observed, missing));
            }
        }
        return (services, partial);
    }

    internal static ClientServiceState NormalizeState(string load, string active, string sub, bool authoritativeMissing = false) =>
        authoritativeMissing && load == "not-found" ? ClientServiceState.Missing :
        load is "error" or "bad-setting" or "not-found" ? ClientServiceState.Unknown :
        active switch
        {
            "active" => ClientServiceState.Running, // active/exited oneshot remains healthy; enablement is independent.
            "inactive" => ClientServiceState.Stopped,
            "failed" => ClientServiceState.Failed,
            "activating" or "reloading" => ClientServiceState.Starting,
            "deactivating" => ClientServiceState.Stopping,
            _ => ClientServiceState.Unknown
        };

    internal static Dictionary<string, string> ParseUnitFiles(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && IsUnitName(parts[0])) result.TryAdd(parts[0], Bounded(parts[1]));
        }
        return result;
    }

    internal static IEnumerable<string> ParseLoadedNames(string output) => output.Split('\n')
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 4 && IsUnitName(parts[0]))
        .Select(parts => parts[0]);

    internal static Dictionary<string, Dictionary<string, string>> ParsePropertyBlocks(string output)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var block = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddBlock()
        {
            if (block.TryGetValue("Id", out var name) && IsUnitName(name))
            {
                result.TryAdd(name, block);
                // Aliases keep their stable inventory identifier even when systemd returns a canonical Id.
                if (block.TryGetValue("Names", out var aliases))
                    foreach (var alias in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (IsUnitName(alias)) result.TryAdd(alias, block);
            }
            block = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) { AddBlock(); continue; }
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator];
            if (Array.IndexOf(Properties, key) >= 0) block[key] = line[(separator + 1)..].TrimEnd('\r');
        }
        AddBlock();
        return result;
    }

    private static bool HasStateProperties(IReadOnlyDictionary<string, string> properties) =>
        properties.ContainsKey("LoadState") && properties.ContainsKey("ActiveState") && properties.ContainsKey("SubState");
    private static bool IsUnitName(string name) => name.EndsWith(".service", StringComparison.Ordinal) && !name.Any(char.IsWhiteSpace) && ClientServiceContractValidator.IsValidServiceName(name, ClientServicePlatform.LinuxSystemd);
    private static bool HasMalformedListRows(string output, int minimumColumns) => output.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Any(parts => parts.Length < minimumColumns || !IsUnitName(parts[0]));
    private static string Value(IReadOnlyDictionary<string, string> properties, string name) => properties.GetValueOrDefault(name) ?? string.Empty;
    private static string Bounded(string? value, string? fallback = null) =>
        BoundText(string.IsNullOrEmpty(value) ? fallback ?? string.Empty : value, ClientServicesLimits.MaximumRawStateLength);
    private static string BoundedDisplayName(string? value, string fallback)
    {
        var text = string.IsNullOrEmpty(value) ? fallback : value;
        return BoundText(text, ClientServicesLimits.MaximumDisplayNameLength);
    }
    private static string BoundText(string value, int maximumLength)
    {
        var length = Math.Min(value.Length, maximumLength);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return new string(value.Take(length).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    }
    private static ClientServiceObservation Unknown(string name, DateTimeOffset observed, string? enablement) =>
        new(name, name, ClientServicePlatform.LinuxSystemd, ClientServiceState.Unknown, string.Empty, null, null, null, null, enablement, observed);
    private static ServiceCollectionResult Result(ServiceSnapshotKind kind, ServiceCollectionStatus status, DateTimeOffset observed,
        IReadOnlyList<ClientServiceObservation> services, string? errorCode = null, ulong revision = 0) =>
        new(Guid.NewGuid(), kind, status, observed, services, errorCode, revision);
}

internal sealed class SystemctlServiceAdapter : ISystemdServiceAdapter
{
    private const int MaximumOutputBytes = 2 * 1024 * 1024;

    public async Task<SystemdCommandResult> QueryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("systemctl")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.Environment["LC_ALL"] = "C";
        start.Environment["SYSTEMD_COLORS"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = cancellationToken.Register(() => Kill(process));
        // Drain stderr with the same bound without ever transmitting diagnostics that may contain sensitive data.
        var errorTask = ReadBoundedAsync(process.StandardError, process, cancellationToken);
        var outputTask = ReadBoundedAsync(process.StandardOutput, process, cancellationToken);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new SystemdCommandResult(process.ExitCode, output.Text, output.Truncated || error.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, Process process, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var bytes = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return (text.ToString(), false);
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
            if (bytes > MaximumOutputBytes) { Kill(process); return (text.ToString(), true); }
            text.Append(buffer, 0, count);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
