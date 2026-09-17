namespace NetRatel.AgentClient;

public sealed record ConfirmationDetails(string ConfirmField, bool RequiredValue, string Operation, IReadOnlyList<string> AffectedIds);
public sealed record ConfirmationRequiredResult(bool Success, string Status, string Summary, bool RequiresConfirmation, ConfirmationDetails Confirmation);

public static class MutationPolicy
{
    public static bool RequiresConfirmation(string tool, string operation) => tool switch
    {
        "netratel_config" => operation is "set" or "unset",
        "netratel_notifications" => operation is "mark_read",
        _ => false
    };

    public static ConfirmationRequiredResult Require(string operation, string summary, params string[] affectedIds) =>
        new(false, "confirmation_required", summary, true, new ConfirmationDetails("confirm", true, operation, affectedIds));
}
