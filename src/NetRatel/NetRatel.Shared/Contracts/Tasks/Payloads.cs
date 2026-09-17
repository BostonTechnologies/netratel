using System.Collections.Generic;
using NetRatel.Shared.Contracts.Execution;

namespace NetRatel.Shared.Contracts.Tasks
{
    public sealed class ExecShellCommandPayload
    {
        public ShellExecutor Preferred { get; set; } = ShellExecutor.Auto;
        public string Command { get; set; } = string.Empty;
        public string? WorkingDirectory { get; set; }
        public int? TimeoutSeconds { get; set; }
        /// <summary>
        /// Names of pre-provisioned client-local environment values required by
        /// this command. Values never traverse an MCP or gateway payload.
        /// </summary>
        public List<string>? EnvironmentReferences { get; set; }
    }

    public sealed class ExecLibraryScriptPayload
    {
        public int ScriptId { get; set; }
        public ScriptType ScriptType { get; set; }
        public string? ScriptContent { get; set; }
        public ShellExecutor Preferred { get; set; } = ShellExecutor.Auto;
        public Dictionary<string, string>? Parameters { get; set; } = new();
        public string? WorkingDirectory { get; set; }
        public int? TimeoutSeconds { get; set; }
    }
}
