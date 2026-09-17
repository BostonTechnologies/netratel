using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace NetRatel.Web.Services;

public sealed class ServerTokenProvider : ITokenProvider
{
    private readonly IHttpContextAccessor _http;

    public ServerTokenProvider(IHttpContextAccessor http) => _http = http;

    public async Task<string?> GetBearerAsync(CancellationToken ct)
    {
        var ctx = _http.HttpContext;
        if (ctx is null)
        {
            return null;
        }

        var token = await ctx.GetTokenAsync("access_token");
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
