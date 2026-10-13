namespace NetRatel.Application.Scripts;

public sealed class ScriptLibraryCollisionException(string message) : InvalidOperationException(message);
