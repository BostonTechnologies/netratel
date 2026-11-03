using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Ganss.Xss;
using HtmlAgilityPack;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

// Full projection permits the UNCHANGED receiver conformance fixtures to be tested,
// although the current Flow sender emits only its existing seven-field subset.
public sealed class ReceiverCreateIncidentBody
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; }
    public string? CustomerId { get; set; }
    public string? OrganizationId { get; set; }
    public string? AssignedToId { get; set; }
    public List<string>? LinkedAssetIds { get; set; }
    public List<string>? Attachments { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Impact { get; set; }
    public List<string>? CcRecipients { get; set; }
    public string? RequesterEmail { get; set; } // Receiver derives customer requester; excluded from hash.
    public List<Guid>? CategoryIds { get; set; }
}

public sealed class RatelDeskReceiverFingerprint : IRatelDeskReceiverFingerprint
{
    private readonly HtmlSanitizer sanitizer = BuildSanitizer();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Compute(RatelDeskCreateIncidentDto body) => ComputeFull(new()
    {
        Title = body.Title, Description = body.Description, Priority = body.Priority,
        CustomerId = body.CustomerId, OrganizationId = body.OrganizationId,
        AssignedToId = body.AssignedToId, CategoryIds = body.CategoryIds.ToList()
    });

    public string ComputeFull(ReceiverCreateIncidentBody body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = body.Title,
            description = Plain(sanitizer.Sanitize(body.Description ?? string.Empty)),
            priority = body.Priority,
            customerId = body.CustomerId,
            organizationId = body.OrganizationId,
            assignedToId = string.IsNullOrWhiteSpace(body.AssignedToId) ? null : body.AssignedToId,
            linkedAssetIds = body.LinkedAssetIds ?? [],
            attachments = body.Attachments ?? [],
            dueDate = DueUtc(body.DueDate)?.ToString("O"),
            impact = body.Impact,
            ccRecipients = Cc(body.CcRecipients),
            categoryIds = Categories(body.CategoryIds).Select(x => x.ToString("D")).ToArray()
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static HtmlSanitizer BuildSanitizer()
    {
        var value = new HtmlSanitizer();
        foreach (var tag in new[] { "img", "table", "thead", "tbody", "tr", "td", "th" }) value.AllowedTags.Add(tag);
        foreach (var attribute in new[] { "src", "style", "class", "alt", "width", "height" }) value.AllowedAttributes.Add(attribute);
        value.AllowedSchemes.Add("data"); return value;
    }
    private static string Plain(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var document = new HtmlDocument(); document.LoadHtml(html);
        var text = WebUtility.HtmlDecode(document.DocumentNode.InnerText);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join('\n', lines);
    }
    private static DateTime? DueUtc(DateTime? value) => value is null ? null : value.Value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value.Value.ToUniversalTime();
    private static List<string> Cc(IEnumerable<string>? values) => values?.Select(value => value.Trim().ToLowerInvariant())
        .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];
    private static List<Guid> Categories(IEnumerable<Guid>? ids) => ids?.Where(x => x != Guid.Empty)
        .Distinct().OrderBy(x => x.ToString("D"), StringComparer.Ordinal).ToList() ?? [];
}
