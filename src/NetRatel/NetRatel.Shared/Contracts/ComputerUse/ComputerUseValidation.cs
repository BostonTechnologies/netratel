namespace NetRatel.Shared.Contracts.ComputerUse;

/// <summary>Wire-level bounds shared by the server, service and interactive helper.</summary>
public static class ComputerUseValidation
{
    private static readonly string[] AllowedKeys =
    ["Enter", "Tab", "Escape", "Backspace", "Delete", "Space", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown"];
    public static string? ValidateCommand(ComputerUseCommand command)
    {
        if (command.ContractVersion != ComputerUseContract.Version) return "unsupported_contract";
        if (command.OperationId == Guid.Empty || !ComputerUseOperations.IsKnown(command.Operation)) return "invalid_operation";
        if (!ComputerUseProviders.IsKnown(command.Provider)) return "unsupported_provider";
        if (command.Target is null || !command.Target.IsSessionTarget) return "invalid_target";
        if (command.Operation is ComputerUseOperations.Capabilities or ComputerUseOperations.ListWindows)
            return command.Action is null ? null : "invalid_action";
        if (command.SessionId == Guid.Empty || command.SessionRevision <= 0) return "invalid_session";
        if (!command.Target.IsWindowTarget) return "invalid_target";
        if (command.Operation != ComputerUseOperations.Act)
            return command.Action is null ? null : "invalid_action";
        if (!command.ControlAllowed) return "control_not_granted";
        if (string.IsNullOrWhiteSpace(command.ObservationId) || command.ObservationId.Length > 256) return "observation_required";
        return ValidateAction(command.Action);
    }

    public static string? ValidateAction(ComputerUseAction? action)
    {
        if (action is null || !ComputerUseActionKinds.IsKnown(action.Kind)) return "unsupported_action";
        if (action.ElementId?.Length > 256 || action.ElementId?.Any(char.IsControl) == true ||
            action.Text?.Length > ComputerUseContract.MaximumTextLength || action.Text?.Contains('\0') == true ||
            action.Key?.Length > 128 || action.Button is not (null or "left" or "right" or "middle") ||
            action.X is < 0 or > 32767 || action.Y is < 0 or > 32767 ||
            action.ScrollX is < -50 or > 50 || action.ScrollY is < -50 or > 50)
            return "action_out_of_bounds";
        var hasCoordinates = action.X is not null || action.Y is not null;
        var hasScroll = action.ScrollX is not null || action.ScrollY is not null;
        var extraneous = action.Kind switch
        {
            ComputerUseActionKinds.Invoke => action.Text is not null || action.Key is not null || hasCoordinates || hasScroll || action.Button is not null,
            ComputerUseActionKinds.Click => action.ElementId is not null || action.Text is not null || action.Key is not null || hasScroll,
            ComputerUseActionKinds.Type => action.Key is not null || hasCoordinates || hasScroll || action.Button is not null,
            ComputerUseActionKinds.Key => action.ElementId is not null || action.Text is not null || hasCoordinates || hasScroll || action.Button is not null,
            ComputerUseActionKinds.Scroll => action.ElementId is not null || action.Text is not null || action.Key is not null || action.Button is not null,
            _ => true
        };
        if (extraneous) return "invalid_action";
        return action.Kind switch
        {
            ComputerUseActionKinds.Invoke when string.IsNullOrWhiteSpace(action.ElementId) => "element_required",
            ComputerUseActionKinds.Click or ComputerUseActionKinds.Scroll when action.X is null || action.Y is null => "coordinates_required",
            ComputerUseActionKinds.Type when string.IsNullOrEmpty(action.Text) => "text_required",
            ComputerUseActionKinds.Key when !AllowedKeys.Contains(action.Key, StringComparer.Ordinal) => "unsupported_key",
            ComputerUseActionKinds.Scroll when (action.ScrollX.GetValueOrDefault() == 0) == (action.ScrollY.GetValueOrDefault() == 0) => "scroll_axis_required",
            _ => null
        };
    }

    public static bool IsMatchingResult(ComputerUseCommand command, ComputerUseResult result) =>
        result.ContractVersion == ComputerUseContract.Version && result.OperationId == command.OperationId &&
        result.SessionId == command.SessionId && (result.Provider is null || result.Provider == command.Provider) &&
        (result.Status is ComputerUseStatuses.Completed or ComputerUseStatuses.Observed or
            ComputerUseStatuses.AppliedUnverified or ComputerUseStatuses.Verified or ComputerUseStatuses.Refused or
            ComputerUseStatuses.Cancelled or ComputerUseStatuses.Failed or ComputerUseStatuses.Unknown) &&
        (result.Code is null || result.Code.Length <= 128 && result.Code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) &&
        (result.Message is null || result.Message.Length <= 4096) &&
        (result.Observation is null || result.Observation.Target == command.Target) &&
        (result.Observation is null || result.Observation.State is { Length: <= ComputerUseContract.MaximumStateLength } &&
            result.Observation.ObservationId is { Length: > 0 and <= 128 } &&
            (result.Observation.Elements is null || result.Observation.Elements.Count <= 512)) &&
        (result.Windows is null || result.Windows.Count <= 256);
}
