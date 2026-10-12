using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Infrastructure.Notifications;

/// <summary>Restricts secret-bearing webhook addresses to the two providers' execution endpoints.</summary>
public static class NotificationWebhookAddress
{
    public const string InvalidAddressCode = "notification-webhook-url-invalid";

    public static Uri Validate(NotificationProvider provider, string webhookUrl)
    {
        if (string.IsNullOrEmpty(webhookUrl) || webhookUrl.Length > NotificationConnectorLimits.MaximumWebhookLength ||
            webhookUrl.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            webhookUrl.IndexOfAny(['%', '\\', '?', '#']) >= 0 ||
            !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort ||
            address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw InvalidAddress();

        // Compare the supplied path before Uri can conceal dot segments or otherwise normalize it.
        var authorityStart = webhookUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = webhookUrl.IndexOf('/', authorityStart);
        if (pathStart < 0 || !string.Equals(webhookUrl[pathStart..], address.AbsolutePath, StringComparison.Ordinal))
            throw InvalidAddress();

        var segments = address.AbsolutePath.Split('/');
        var valid = provider switch
        {
            NotificationProvider.Discord => address.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) &&
                IsDiscordPath(segments),
            NotificationProvider.Slack =>
                (address.Host.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase) ||
                 address.Host.Equals("hooks.slack-gov.com", StringComparison.OrdinalIgnoreCase)) &&
                segments is ["", "services", var workspace, var hook, var token] &&
                IsToken(workspace) && IsToken(hook) && IsToken(token),
            _ => false
        };
        if (!valid) throw InvalidAddress();

        return new UriBuilder(address) { Host = address.Host.ToLowerInvariant(), Port = -1 }.Uri;
    }

    internal static bool IsSnowflake(string value) => value.Length is > 0 and <= 20 &&
        value.All(char.IsAsciiDigit) && ulong.TryParse(value, out var id) && id > 0;

    private static bool IsDiscordPath(string[] segments) => segments switch
    {
        ["", "api", "webhooks", var id, var token] => IsSnowflake(id) && IsToken(token),
        ["", "api", "v10", "webhooks", var id, var token] => IsSnowflake(id) && IsToken(token),
        _ => false
    };

    private static bool IsToken(string value) => value.Length is > 0 and <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static ArgumentException InvalidAddress() => new(InvalidAddressCode, "webhookUrl");
}
