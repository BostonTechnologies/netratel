namespace NetRatel.Web.Services;

public interface ITokenProvider
{
    Task<string?> GetBearerAsync(CancellationToken ct);
}
