using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Web.Services.Flows;

/// <summary>Bounded local graph history. View navigation and saved definition revisions are not history state.</summary>
public sealed class FlowEditorHistory
{
    public const int MaximumEntries = 32;
    private readonly List<FlowEditorHistoryEntry> _entries = [];
    private int _cursor = -1;
    private string? _group;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor >= 0 && _cursor < _entries.Count - 1;
    public int Count => _entries.Count;

    public void Reset(string name, FlowGraphDto graph, Guid? selection)
    {
        _entries.Clear(); _cursor = -1; _group = null; Record(name, graph, selection);
    }
    public void BreakGroup() => _group = null;
    public void Clear() { _entries.Clear(); _cursor = -1; _group = null; }
    public void Record(string name, FlowGraphDto graph, Guid? selection, string? group = null)
    {
        // Keeping the current viewport on restore avoids undoing zoom, pan or window resize.
        var snapshot = graph with { Viewport = new() };
        var json = graph.SchemaVersion == FlowGraphV2Limits.SchemaVersion ? FlowGraphSerialization.Serialize(snapshot) : JsonSerializer.Serialize(snapshot);
        var fingerprint = name + "\n" + json;
        var entry = new FlowEditorHistoryEntry(fingerprint, name.Length, selection);
        if (_cursor >= 0 && _entries[_cursor].Fingerprint == fingerprint)
        { _entries[_cursor] = entry; return; }
        if (_cursor + 1 < _entries.Count) _entries.RemoveRange(_cursor + 1, _entries.Count - _cursor - 1);
        if (group is not null && group == _group && _cursor > 0)
        {
            if (_entries[_cursor - 1].Fingerprint == fingerprint)
            { _entries.RemoveAt(_cursor); _cursor--; _entries[_cursor] = entry; _group = null; return; }
            _entries[_cursor] = entry;
        }
        else { _entries.Add(entry); _cursor++; }
        _group = group;
        if (_entries.Count > MaximumEntries) { _entries.RemoveAt(0); _cursor--; }
    }
    public FlowEditorHistoryEntry? PeekUndo(Func<FlowGraphDto, bool> canRestore) => Find(-1, canRestore);
    public FlowEditorHistoryEntry? PeekRedo(Func<FlowGraphDto, bool> canRestore) => Find(1, canRestore);
    public void Accept(FlowEditorHistoryEntry entry)
    {
        var index = _entries.IndexOf(entry);
        if (index < 0) throw new InvalidOperationException("The local history context changed.");
        _cursor = index; _group = null;
    }
    public FlowEditorHistoryEntry? Undo(Func<FlowGraphDto, bool> canRestore)
    { var entry = PeekUndo(canRestore); if (entry is not null) Accept(entry); return entry; }
    public FlowEditorHistoryEntry? Redo(Func<FlowGraphDto, bool> canRestore)
    { var entry = PeekRedo(canRestore); if (entry is not null) Accept(entry); return entry; }
    private FlowEditorHistoryEntry? Find(int direction, Func<FlowGraphDto, bool> canRestore)
    {
        for (var next = _cursor + direction; next >= 0 && next < _entries.Count; next += direction)
        {
            var entry = _entries[next];
            // Incomplete form edits can be recorded but cannot replace the inert canvas until safe.
            if (!canRestore(entry.Graph)) continue;
            return entry;
        }
        return null;
    }
}

public sealed record FlowEditorHistoryEntry(string Fingerprint, int NameLength, Guid? SelectedNodeId)
{
    private static readonly JsonSerializerOptions ReadJson = new(JsonSerializerDefaults.Web);
    public string Name => Fingerprint[..NameLength];
    public FlowGraphDto Graph => JsonSerializer.Deserialize<FlowGraphDto>(Fingerprint[(NameLength + 1)..], ReadJson)
        ?? throw new InvalidOperationException("The local graph history is unavailable.");
}
