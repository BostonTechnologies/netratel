using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NetRatel.Shared.Service.Logging;

/// <summary>The diagnostic threshold shared by hosted services, stdio MCP and the native Client.</summary>
public sealed class NetRatelLoggingOptions
{
    public const string SectionName = "NetRatel:Logging";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;
    public Dictionary<string, LogLevel> Overrides { get; set; } = new(StringComparer.Ordinal);

    public static NetRatelLoggingOptions Load(IConfiguration configuration)
    {
        NetRatelLoggingOptions options;
        try
        {
            options = configuration.GetSection(SectionName).Get<NetRatelLoggingOptions>() ?? new();
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("NetRatel:Logging levels must be Trace, Debug, Information, Warning, Error, Critical or None.");
        }
        options.EnsureValid();
        return options;
    }

    public bool IsValid() => Enum.IsDefined(MinimumLevel) && Overrides is { Count: <= 64 } &&
        Overrides.All(pair => pair.Key.Length is > 0 and <= 128 && !pair.Key.Any(char.IsControl) && Enum.IsDefined(pair.Value));

    public void EnsureValid()
    {
        if (!IsValid())
            throw new InvalidOperationException("NetRatel:Logging requires named valid levels and at most 64 nonempty category overrides of at most 128 characters.");
    }

    public LogLevel Threshold(string? category)
    {
        var level = MinimumLevel;
        var longest = -1;
        foreach (var (prefix, candidate) in Overrides)
            if (category is not null && category.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > longest)
            {
                longest = prefix.Length;
                level = candidate;
            }
        // These providers log bearer capability paths. Diagnostic opt-ins must
        // never enable their request-start/forwarder records.
        if (IsCapabilityCategory(category) && level < LogLevel.Warning) level = LogLevel.Warning;
        return level;
    }

    public bool IsEnabled(string? category, LogLevel level) => level != LogLevel.None && level >= Threshold(category);

    public static bool IsCapabilityCategory(string? category) => category is not null &&
        (category.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal) ||
         category.StartsWith("Yarp.ReverseProxy", StringComparison.Ordinal));
}
