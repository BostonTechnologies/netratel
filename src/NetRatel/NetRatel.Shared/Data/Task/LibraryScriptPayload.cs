
namespace NetRatel.Shared.Data.Task;

// Define a helper class for the structured payload
public record LibraryScriptPayload(ulong ScriptId, Dictionary<string, string> Parameters);
