namespace NetRatel.Shared.Contracts;

/// <summary>Operator-managed labels, separate from the enrolled identity and reported hostname.</summary>
public sealed record ClientMetadataDto(
    int TenantId,
    Guid AgentId,
    string? HostName,
    string? FriendlyName,
    IReadOnlyList<string> Tags);

public sealed record UpdateClientMetadataRequest(string? FriendlyName, IReadOnlyList<string>? Tags);

public static class ClientMetadataValidation
{
    public const int MaximumNameLength = 128;
    public const int MaximumTagLength = 48;
    public const int MaximumTags = 20;

    public static bool TryNormalize(UpdateClientMetadataRequest request,
        out UpdateClientMetadataRequest normalized, out string? error)
    {
        normalized = new(null, []);
        error = null;
        if (request.FriendlyName?.Any(char.IsControl) == true)
        {
            error = "The friendly name cannot contain control characters.";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(request.FriendlyName) ? null : request.FriendlyName.Trim();
        if (name?.Length > MaximumNameLength)
        {
            error = $"The friendly name must be {MaximumNameLength} characters or fewer.";
            return false;
        }

        if (request.Tags?.Count > MaximumTags)
        {
            error = $"Use no more than {MaximumTags} tags.";
            return false;
        }

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in request.Tags ?? [])
        {
            if (value?.Any(char.IsControl) == true || value?.Contains(',') == true)
            {
                error = "Tags cannot contain commas or control characters.";
                return false;
            }

            var tag = value?.Trim();
            if (string.IsNullOrEmpty(tag)) continue;
            if (tag.Length > MaximumTagLength)
            {
                error = $"Each tag must be {MaximumTagLength} characters or fewer.";
                return false;
            }
            if (seen.Add(tag)) tags.Add(tag);
        }

        normalized = new(name, tags.ToArray());
        return true;
    }
}
