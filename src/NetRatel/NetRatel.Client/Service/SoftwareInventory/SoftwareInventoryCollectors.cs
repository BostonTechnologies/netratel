using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.Client.Service.SoftwareInventory;

public interface ISoftwareInventoryCollector
{
    Task<SoftwareCollection> CollectAsync(CancellationToken ct);
}

public sealed class UnsupportedSoftwareInventoryCollector : ISoftwareInventoryCollector
{
    public Task<SoftwareCollection> CollectAsync(CancellationToken ct) => Task.FromResult(new SoftwareCollection([], false, "unsupported", ["operating_system_unsupported"]));
}

public sealed record SoftwareCommandResult(int ExitCode, string Output, bool Truncated = false);
public interface ISoftwareCommandRunner
{
    bool Exists(string executable);
    Task<SoftwareCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>Fixed argument vectors, bounded output and deadlines; no shell or package modifications.</summary>
public sealed class SoftwareCommandRunner : ISoftwareCommandRunner
{
    public bool Exists(string executable) => Resolve(executable) is not null;
    private static string? Resolve(string executable) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
        .Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists);

    public async Task<SoftwareCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = new Process { StartInfo = new ProcessStartInfo(Resolve(executable) ?? executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.StartInfo.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } catch (Win32Exception) { } });
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError, timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var result = await output.ConfigureAwait(false);
        await error.ConfigureAwait(false);
        return new(process.ExitCode, result.Text, result.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        const int maximum = 32 * 1024 * 1024;
        var output = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var available = Math.Min(count, maximum - output.Length);
            output.Append(buffer, 0, available);
            truncated |= available != count;
        }
        return (output.ToString(), truncated);
    }
}

public sealed class LinuxSoftwareInventoryCollector(ISoftwareCommandRunner? runner = null) : ISoftwareInventoryCollector
{
    private readonly ISoftwareCommandRunner _runner = runner ?? new SoftwareCommandRunner();

    public async Task<SoftwareCollection> CollectAsync(CancellationToken ct)
    {
        var packages = new List<SoftwarePackage>();
        var errors = new List<string>();
        var supported = false;
        await Query("dpkg-query", ["-W", "-f=${binary:Package}\t${Version}\t${Maintainer}\t${Architecture}\t${db:Status-Status}\n"], "dpkg");
        await Query("rpm", ["-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\t%{ARCH}\n"], "rpm");
        await Query("pacman", ["-Q"], "pacman");
        await Query("snap", ["list"], "snap");
        await Query("flatpak", ["list", "--system", "--columns=application,name,version,arch,origin"], "flatpak");
        await Query("flatpak", ["list", "--user", "--columns=application,name,version,arch,origin"], "flatpak", "user");
        if (!supported) errors.Add("no_supported_package_manager");
        if (packages.Count > SoftwareInventoryWire.PackagesPerChunk * SoftwareInventoryWire.MaximumChunks)
        {
            packages = packages.Take(SoftwareInventoryWire.PackagesPerChunk * SoftwareInventoryWire.MaximumChunks).ToList();
            errors.Add("inventory_truncated");
        }
        return new(packages, errors.Count == 0, "linux", errors.Distinct().ToArray());

        async Task Query(string executable, string[] args, string source, string scope = "machine")
        {
            if (!_runner.Exists(executable)) return;
            supported = true;
            try
            {
                var result = await _runner.RunAsync(executable, args, ct).ConfigureAwait(false);
                if (result.ExitCode != 0 || result.Truncated) errors.Add(source + "_scan_failed");
                var parsed = Parse(source, result.Output, scope);
                packages.AddRange(parsed.Packages);
                errors.AddRange(parsed.Errors);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or ArgumentException)
            { errors.Add(source + "_scan_failed"); }
        }
    }

    public static SoftwareCollection Parse(string source, string output, string scope = "machine")
    {
        var packages = new List<SoftwarePackage>();
        var invalid = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line) || source == "snap" && line.StartsWith("Name ", StringComparison.Ordinal)) continue;
            var fields = source is "snap" or "pacman" ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : line.Split('\t');
            try
            {
                SoftwarePackage? package = source switch
                {
                    "dpkg" when fields.Length == 5 && fields[4].Trim() != "installed" => null,
                    "dpkg" when fields.Length == 5 => new(fields[0], fields[1], fields[2], fields[3], fields[0], source, Scope: scope),
                    "rpm" when fields.Length == 4 => new(fields[0], fields[1], fields[2] == "(none)" ? "" : fields[2], fields[3], fields[0], source, Scope: scope),
                    "pacman" when fields.Length == 2 => new(fields[0], fields[1], "", RuntimeInformation.OSArchitecture.ToString(), fields[0], source, Scope: scope),
                    "snap" when fields.Length >= 6 => new(fields[0], fields[1], fields[4], RuntimeInformation.OSArchitecture.ToString(), fields[0], source, Scope: scope),
                    "flatpak" when fields.Length == 5 => new(fields[1], fields[2], fields[4], fields[3], fields[0], source, Scope: scope),
                    _ => throw new ArgumentException("Malformed package row.")
                };
                if (package is not null) packages.Add(package.Normalize());
            }
            catch (ArgumentException) { invalid = true; }
        }
        return new(packages, !invalid, "linux", invalid ? [source + "_parse_failed"] : []);
    }
}
