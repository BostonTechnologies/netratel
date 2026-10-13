using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Scripts;

namespace NetRatel.Web.Components.ScriptLibrary;

public class NewScriptDialogModel
{
    public string? Name { get; set; }
    public string? Folder { get; set; }
    public ScriptType? Type { get; set; }
    public IReadOnlyList<string> ExistingFolders { get; set; } = ["/"];
    public bool UseNewFolder { get; set; }
    public string? NewFolder { get; set; }

    public string PlacementPreview
    {
        get
        {
            try { return ScriptLibraryPlacement.NormalizeFolder(UseNewFolder ? NewFolder : Folder) + (Name?.Trim() ?? string.Empty); }
            catch (ArgumentException) { return "Enter a valid folder path to preview placement."; }
        }
    }

    public CreateScriptRequest ToRequest()
    {
        if (Type is null || !ScriptLibraryTypes.Options.Any(option => option.Value == Type))
            throw new ArgumentException("Choose a script type.");
        if (UseNewFolder && string.IsNullOrWhiteSpace(NewFolder))
            throw new ArgumentException("Enter a new folder path.");
        return new(ScriptLibraryPlacement.NormalizeName(Name),
            ScriptLibraryPlacement.NormalizeFolder(UseNewFolder ? NewFolder : Folder), "", "", Type.Value.ToString());
    }
}

public class RenameScriptDialogModel
{
    public string? Name { get; set; }
}
