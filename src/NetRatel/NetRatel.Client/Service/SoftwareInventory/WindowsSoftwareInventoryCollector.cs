using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.Client.Service.SoftwareInventory;

public sealed record WindowsSoftwareRegistryEntry(string Key, string Architecture, string Scope, IReadOnlyDictionary<string, string> Values);
public sealed record WindowsSoftwareRegistryResult(IReadOnlyList<WindowsSoftwareRegistryEntry> Entries, IReadOnlyList<string> Errors);
public interface IWindowsSoftwareRegistry
{
    WindowsSoftwareRegistryResult Read();
}

public sealed class WindowsSoftwareInventoryCollector(IWindowsSoftwareRegistry? registry = null, ISoftwareCommandRunner? runner = null) : ISoftwareInventoryCollector
{
    private readonly IWindowsSoftwareRegistry _registry = registry ?? new WindowsSoftwareRegistry();
    private readonly ISoftwareCommandRunner _runner = runner ?? new SoftwareCommandRunner();

    public async Task<SoftwareCollection> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = _registry.Read();
        var errors = result.Errors.ToList();
        var packages = new List<SoftwarePackage>();
        foreach (var entry in result.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try { if (FromRegistry(entry) is { } package) packages.Add(package); }
            catch (ArgumentException) { errors.Add("registry_metadata_invalid"); }
        }
        if (_runner.Exists("powershell.exe"))
        {
            try
            {
                var appx = await _runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                    "$ErrorActionPreference='Stop'; if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) { Get-AppxPackage -AllUsers | Select-Object Name,Version,Publisher,@{n='Architecture';e={$_.Architecture.ToString()}},PackageFamilyName,InstallLocation | ConvertTo-Json -Compress }"], ct).ConfigureAwait(false);
                if (appx.ExitCode != 0 || appx.Truncated) errors.Add("appx_scan_failed");
                else if (!string.IsNullOrWhiteSpace(appx.Output))
                {
                    using var document = JsonDocument.Parse(appx.Output);
                    var elements = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
                    foreach (var item in elements)
                    {
                        string Value(string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
                        packages.Add(new SoftwarePackage(Value("Name"), Value("Version"), Value("Publisher"), Value("Architecture"),
                            Value("PackageFamilyName"), "appx", Value("InstallLocation")).Normalize());
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or JsonException or ArgumentException or OperationCanceledException)
            { errors.Add("appx_scan_failed"); }
        }
        if (packages.Count > SoftwareInventoryWire.PackagesPerChunk * SoftwareInventoryWire.MaximumChunks)
        {
            packages = packages.Take(SoftwareInventoryWire.PackagesPerChunk * SoftwareInventoryWire.MaximumChunks).ToList();
            errors.Add("inventory_truncated");
        }
        return new(packages, errors.Count == 0, "windows", errors.Distinct().Take(32).ToArray());
    }

    public static SoftwarePackage? FromRegistry(WindowsSoftwareRegistryEntry entry)
    {
        string Value(string name) => entry.Values.TryGetValue(name, out var value) ? value : "";
        if (string.IsNullOrWhiteSpace(Value("DisplayName"))) return null;
        return new SoftwarePackage(Value("DisplayName"), Value("DisplayVersion"), Value("Publisher"), entry.Architecture,
            entry.Key, "registry", Value("InstallLocation"), Value("InstallDate"), entry.Scope).Normalize();
    }
}

public sealed class WindowsSoftwareRegistry : IWindowsSoftwareRegistry
{
    public WindowsSoftwareRegistryResult Read()
    {
        if (!OperatingSystem.IsWindows()) return new([], ["windows_registry_unavailable"]);
        return ReadWindows();
    }

    [SupportedOSPlatform("windows")]
    private static WindowsSoftwareRegistryResult ReadWindows()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        string[] fields = ["DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "InstallDate"];
        var entries = new List<WindowsSoftwareRegistryEntry>();
        var errors = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            if (view == RegistryView.Registry64 && !Environment.Is64BitOperatingSystem) continue;
            var architecture = view == RegistryView.Registry32 ? "x86" : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                ReadKey(machine, uninstall, architecture, "machine");
                using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
                foreach (var sid in users.GetSubKeyNames().Where(name => name.StartsWith("S-1-", StringComparison.Ordinal) && !name.EndsWith("_Classes", StringComparison.Ordinal)))
                    ReadKey(users, sid + "\\" + uninstall, architecture, sid);
                // HKCU is also included for interactive/non-service installations.
                using var current = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                ReadKey(current, uninstall, architecture, System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "current-user");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
            { errors.Add("registry_scan_denied"); }
        }
        return new(entries, errors.Distinct().ToArray());

        void ReadKey(RegistryKey root, string path, string architecture, string scope)
        {
            try
            {
                using var parent = root.OpenSubKey(path, false);
                if (parent is null) return;
                foreach (var name in parent.GetSubKeyNames())
                {
                    try
                    {
                        using var key = parent.OpenSubKey(name, false);
                        if (key is null) { errors.Add("registry_scan_interrupted"); continue; }
                        // Read only this allowlist; registration/license values are never collected.
                        var values = fields.ToDictionary(field => field, field => key.GetValue(field, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "");
                        entries.Add(new(name, architecture, scope, values));
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
                    { errors.Add("registry_scan_denied"); }
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
            { errors.Add("registry_scan_denied"); }
        }
    }
}
