using NetRatel.Shared.Contracts.Scripts;

namespace NetRatel.Web.Components.ScriptLibrary;

/// <summary>The selected script's draft, retained when its editor moves into or out of a dialog.</summary>
public sealed class ScriptEditorDraft(ScriptDto script)
{
    public ScriptDto Saved { get; private set; } = script;
    public string Name { get; set; } = script.Name;
    public string Content { get; set; } = script.Content ?? string.Empty;
    public string? Error { get; set; }
    public bool Saving { get; set; }
    public bool Deleting { get; set; }
    public bool Busy => Saving || Deleting;
    public bool IsDirty => !string.Equals(Name, Saved.Name, StringComparison.Ordinal)
        || !string.Equals(Content, Saved.Content ?? string.Empty, StringComparison.Ordinal);

    public void AcceptSave(ScriptDto saved, string submittedName)
    {
        // A later edit made while the request was in flight remains a dirty draft.
        if (Name == submittedName) Name = saved.Name;
        Saved = saved;
        Error = null;
    }
}
