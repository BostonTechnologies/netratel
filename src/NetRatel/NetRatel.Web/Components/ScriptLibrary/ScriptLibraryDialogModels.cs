using NetRatel.Shared.Contracts.Execution;

namespace NetRatel.Web.Components.ScriptLibrary;

public class NewScriptDialogModel
{
    public string? Name { get; set; }
    public string? Folder { get; set; }
    public ScriptType? Type { get; set; }
}

public class RenameScriptDialogModel
{
    public string? Name { get; set; }
}
