namespace NetRatel.Shared.Contracts.Monitoring;

public static class MonitoringAuthoringGuidance
{
    public const string Text = " Numeric conditions require explicit resourceName and servicePlatform JSON nulls where applicable, and expectedServiceStates:[]. CPU requires resourceName:null, Percent, and breach > recovery. Disk uses resourceName:null for all eligible disks or an exact normalized path; empty strings and * are invalid. DiskFreePercent measures FREE space: 80%/90% used = 20%/10% free, with breach < recovery. Required nullable constructor fields must be present as null, not omitted. Use preview_targets with the same targets/condition/freshness to recover; it reads cached support without changing configuration or starting collection. Known failures include validation.code, validation.field and bounded remediation; correct that field and preview again. Read configuration/revisions before saving; do not change resetPolicy to work around condition errors.";
}
