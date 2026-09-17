using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetRatel.Application.Notifications;

public sealed class NetRatelNotificationDisplaySanitizer(IClientDisplayNameResolver clientDisplayNameResolver)
{
    private const string ClientUnavailable = "Client unavailable";
    private static readonly Regex ClientIdentityRegex = new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);
    private readonly IClientDisplayNameResolver _clientDisplayNameResolver = clientDisplayNameResolver;

    public IReadOnlyList<NetRatelNotificationDto> SanitizeMany(IEnumerable<NetRatelNotificationDto> notifications)
    {
        var items = notifications.ToList();
        if (items.Count == 0)
        {
            return items;
        }

        var identities = items.SelectMany(ExtractClientIdentities).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var displayNames = _clientDisplayNameResolver.ResolveDisplayNames(identities);

        return items.Select(item => Sanitize(item, displayNames)).ToList();
    }

    public NetRatelNotificationDto Sanitize(NetRatelNotificationDto notification)
    {
        var displayNames = _clientDisplayNameResolver.ResolveDisplayNames(ExtractClientIdentities(notification));
        return Sanitize(notification, displayNames);
    }

    private static NetRatelNotificationDto Sanitize(
        NetRatelNotificationDto notification,
        IReadOnlyDictionary<string, string> displayNames)
    {
        return notification with
        {
            CorrelationId = ReplaceClientIdentities(notification.CorrelationId, displayNames) ?? string.Empty,
            EntityId = ReplaceClientIdentities(notification.EntityId, displayNames),
            Message = ReplaceClientIdentities(notification.Message, displayNames),
            PayloadJson = SanitizePayloadJson(notification.PayloadJson, displayNames)
        };
    }

    private static IEnumerable<string> ExtractClientIdentities(NetRatelNotificationDto notification)
    {
        foreach (var identity in ExtractClientIdentities(notification.CorrelationId))
        {
            yield return identity;
        }

        foreach (var identity in ExtractClientIdentities(notification.EntityId))
        {
            yield return identity;
        }

        foreach (var identity in ExtractClientIdentities(notification.Message))
        {
            yield return identity;
        }

        foreach (var identity in ExtractClientIdentities(notification.PayloadJson))
        {
            yield return identity;
        }
    }

    private static IEnumerable<string> ExtractClientIdentities(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (Match match in ClientIdentityRegex.Matches(value))
        {
            yield return match.Value;
        }
    }

    private static string? ReplaceClientIdentities(
        string? value,
        IReadOnlyDictionary<string, string> displayNames)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return ClientIdentityRegex.Replace(value, match => ResolveDisplayName(match.Value, displayNames));
    }

    private static string SanitizePayloadJson(
        string payloadJson,
        IReadOnlyDictionary<string, string> displayNames)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return payloadJson;
        }

        try
        {
            var root = JsonNode.Parse(payloadJson);
            if (root is null)
            {
                return ReplaceClientIdentities(payloadJson, displayNames) ?? payloadJson;
            }

            SanitizeNode(root, displayNames);
            return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return ReplaceClientIdentities(payloadJson, displayNames) ?? payloadJson;
        }
    }

    private static void SanitizeNode(JsonNode node, IReadOnlyDictionary<string, string> displayNames)
    {
        if (node is JsonObject obj)
        {
            var replacements = new List<(string OldName, string NewName, JsonNode? Value)>();

            foreach (var property in obj.ToList())
            {
                if (property.Value is null)
                {
                    continue;
                }

                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var stringValue)
                    && ClientIdentityRegex.IsMatch(stringValue))
                {
                    var displayValue = ReplaceClientIdentities(stringValue, displayNames) ?? ClientUnavailable;
                    var newName = IsClientIdentityProperty(property.Key)
                        ? "client"
                        : property.Key;
                    replacements.Add((property.Key, newName, JsonValue.Create(displayValue)));
                    continue;
                }

                SanitizeNode(property.Value, displayNames);
            }

            foreach (var replacement in replacements)
            {
                obj.Remove(replacement.OldName);
                obj[replacement.NewName] = replacement.Value;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    SanitizeNode(child, displayNames);
                }
            }
        }
    }

    private static string ResolveDisplayName(
        string clientIdentity,
        IReadOnlyDictionary<string, string> displayNames)
        => displayNames.TryGetValue(clientIdentity, out var displayName)
            && !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : ClientUnavailable;

    private static bool IsClientIdentityProperty(string propertyName)
        => string.Equals(propertyName, "clientIdentity", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "targetClientIdentity", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "clientId", StringComparison.OrdinalIgnoreCase);
}
