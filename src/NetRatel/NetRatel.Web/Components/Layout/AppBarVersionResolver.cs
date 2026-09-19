using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace NetRatel.Web.Components.Layout;

public static class AppBarVersionResolver
{
    public static string Resolve(IConfiguration configuration, Assembly? assembly = null)
        => ResolveDetails(configuration, assembly).DisplayVersion;

    public static AppBarVersionDetails ResolveDetails(IConfiguration _, Assembly? assembly = null)
    {
        var assemblyVersion = ResolveAssemblyVersion(assembly);
        // Product identity is compiled into this application. Deployment counters,
        // runtime VERSION variables, and telemetry resource attributes describe
        // their own systems and must never relabel the product in the UI.
        var value = assemblyVersion ?? "dev";

        return new AppBarVersionDetails(
            ServiceName: "NetRatel.Web",
            DisplayVersion: FormatProductVersion(value),
            FullVersion: value.Trim(),
            Source: assemblyVersion is null ? "Fallback" : "AssemblyInformationalVersion",
            AssemblyVersion: assembly?.GetName().Version?.ToString() ?? "unknown");
    }

    private static string? ResolveAssemblyVersion(Assembly? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString();
    }

    public static string FormatProductVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        var metadataStart = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (metadataStart >= 0)
        {
            trimmed = trimmed[..metadataStart];
        }

        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"v{trimmed}";
    }
}

public sealed record AppBarVersionDetails(
    string ServiceName,
    string DisplayVersion,
    string FullVersion,
    string Source,
    string AssemblyVersion);
