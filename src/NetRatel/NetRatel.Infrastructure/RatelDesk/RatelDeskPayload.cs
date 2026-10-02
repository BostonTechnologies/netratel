using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public static partial class RatelDeskPayload
{
    public static RatelDeskCreateIncidentDto Create(RatelDeskConnectorConfiguration configuration, string title, string description, int priority)
    {
        if (priority is < 0 or > 3) throw new ArgumentException("invalid-incident-priority");
        var safeTitle = Plain(title, RatelDeskConnectorLimits.MaximumTitleCharacters, false);
        var safeDescription = Plain(description, RatelDeskConnectorLimits.MaximumDescriptionCharacters, true);
        if (safeTitle.Length == 0 || safeDescription.Length == 0) throw new ArgumentException("incident-fields-required");
        return new(safeTitle, safeDescription, priority, configuration.CustomerId, configuration.OrganizationId,
            configuration.AssignedToId, configuration.CategoryIds.Order().ToArray());
    }

    public static string Fingerprint(RatelDeskCreateIncidentDto payload) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));

    private static string Plain(string? value, int limit, bool multiline)
    {
        if (value is null || value.Length > limit * 4) throw new ArgumentException("incident-field-too-large");
        var text = Tags().Replace(System.Net.WebUtility.HtmlDecode(value), string.Empty);
        text = new string(text.Where(character => !char.IsControl(character) || multiline && character is '\n' or '\r').ToArray()).Trim();
        if (text.Length > limit) throw new ArgumentException("incident-field-too-large");
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i])) throw new ArgumentException("invalid-incident-text");
            if (char.IsHighSurrogate(text[i]) && (++i >= text.Length || !char.IsLowSurrogate(text[i]))) throw new ArgumentException("invalid-incident-text");
        }
        return text;
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Tags();
}
