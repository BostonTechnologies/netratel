using System.Collections.Generic;
using NetRatel.Shared.Contracts.Execution;

namespace NetRatel.Shared.Contracts.Tasks
{
    public sealed class TaskCreateRequestDto
    {
        /// <summary>Current execution target. This, together with <see cref="TenantId"/>, is authoritative.</summary>
        public Guid? AgentId { get; set; }

        /// <summary>Historical compatibility input. V2 task creation deliberately ignores it.</summary>
        public string ClientIdentity { get; set; } = string.Empty;
        public int? TenantId { get; set; }
        public ClientEnvironment Environment { get; set; }
        public string TaskType { get; set; } = string.Empty;
        public ExecShellCommandPayload? ShellCommand { get; set; }
        public int? ScriptId { get; set; }
        public ScriptType? ScriptType { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
        public ShellExecutor Preferred { get; set; } = ShellExecutor.Auto;
        public string? WorkingDirectory { get; set; }
        public int? TimeoutSeconds { get; set; }
        public string? RequestId { get; set; }
        public string? Payload { get; set; }
    }
}
