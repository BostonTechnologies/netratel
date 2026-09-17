namespace NetRatel.API.Models;

public static class ClientScriptValidation
{
    public const int MinValidityMinutes = 5;
    public const int MaxValidityMinutes = 10080;

    public static (int tenantId, string normalizedRid, int validForMinutes, int maxUses) Validate(ClientScriptRequest req, bool isWindows)
    {
        if (req.TenantId <= 0)
        {
            throw new RequestValidationException("tenantId", "TenantId must be a positive integer.");
        }

        var rid = ClientDownloadValidation.NormalizeRid(req.RuntimeId, isWindows);
        if (string.IsNullOrWhiteSpace(rid))
        {
            throw new RequestValidationException("runtimeId", "RuntimeId must be a valid RID (e.g., 'win-x64', 'linux-x64').");
        }

        if (req.ValidForMinutes < MinValidityMinutes || req.ValidForMinutes > MaxValidityMinutes)
        {
            throw new RequestValidationException("validForMinutes", $"ValidForMinutes must be between {MinValidityMinutes} and {MaxValidityMinutes}.");
        }

        var maxUses = req.MaxUses ?? 1;
        if (maxUses <= 0)
        {
            throw new RequestValidationException("maxUses", "MaxUses must be greater than zero.");
        }

        return (req.TenantId, rid, req.ValidForMinutes, maxUses);
    }
}
