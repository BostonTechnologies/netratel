namespace NetRatel.Application.Events;

public static class NetRatelEventTypes
{
    public static class System
    {
        public const string UnhandledException = "DomainEvent.System.UnhandledException";
        public const string AgentTokenIssueFailed = "DomainEvent.System.AgentTokenIssueFailed";
    }

    public static class Tenant
    {
        public const string Created = "DomainEvent.Orchestration.Tenant.Created";
        public const string Updated = "DomainEvent.Orchestration.Tenant.Updated";
        public const string Deleted = "DomainEvent.Orchestration.Tenant.Deleted";
    }

    public static class Client
    {
        public const string Created = "DomainEvent.Orchestration.Client.Created";
        public const string Updated = "DomainEvent.Orchestration.Client.Updated";
        public const string Deleted = "DomainEvent.Orchestration.Client.Deleted";
        public const string Connected = "DomainEvent.Orchestration.Client.Connected";
        public const string Disconnected = "DomainEvent.Orchestration.Client.Disconnected";
        public const string StateChanged = "DomainEvent.Orchestration.Client.StateChanged";
    }

    public static class Script
    {
        public const string Created = "DomainEvent.Orchestration.Script.Created";
        public const string Updated = "DomainEvent.Orchestration.Script.Updated";
        public const string Deleted = "DomainEvent.Orchestration.Script.Deleted";
    }

    public static class Job
    {
        public const string Submitted = "DomainEvent.Orchestration.Job.Submitted";
        public const string Created = "DomainEvent.Orchestration.Job.Created";
        public const string Scheduled = "DomainEvent.Orchestration.Job.Scheduled";
        public const string Started = "DomainEvent.Orchestration.Job.Started";
        public const string StateChanged = "DomainEvent.Orchestration.Job.StateChanged";
        public const string Completed = "DomainEvent.Orchestration.Job.Completed";
    }

    public static class Task
    {
        public const string Submitted = "DomainEvent.Orchestration.Task.Submitted";
        public const string Started = "DomainEvent.Orchestration.Task.Started";
        public const string StateChanged = "DomainEvent.Orchestration.Task.StateChanged";
        public const string Completed = "DomainEvent.Orchestration.Task.Completed";
    }

    public static class Artifact
    {
        public const string Uploaded = "DomainEvent.Orchestration.Artifact.Uploaded";
        public const string Deleted = "DomainEvent.Orchestration.Artifact.Deleted";
    }

    public static class Agent
    {
        public const string Registered = "DomainEvent.Orchestration.Agent.Registered";
        public const string IdentityRecovered = "DomainEvent.Orchestration.Agent.IdentityRecovered";
        public const string Superseded = "DomainEvent.Orchestration.Agent.Superseded";
        public const string Connected = "DomainEvent.Orchestration.Agent.Connected";
        public const string Disconnected = "DomainEvent.Orchestration.Agent.Disconnected";
        public const string WentOffline = "DomainEvent.Orchestration.Agent.WentOffline";
    }

    public static class AgentToken
    {
        public const string Issued = "DomainEvent.Orchestration.AgentToken.Issued";
        public const string Rotated = "DomainEvent.Orchestration.AgentToken.Rotated";
        public const string RotationRecovered = "DomainEvent.Orchestration.AgentToken.RotationRecovered";
        public const string Revoked = "DomainEvent.Orchestration.AgentToken.Revoked";
    }
}
