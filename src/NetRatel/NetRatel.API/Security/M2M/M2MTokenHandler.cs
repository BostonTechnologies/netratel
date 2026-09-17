using System.Net.Http.Headers;

namespace NetRatel.API.Security.M2M;

internal sealed class M2MTokenHandler : DelegatingHandler
{
    private readonly IClientCredentialsTokenService _tokens;
    private readonly DownstreamApiOptions _target;

    public M2MTokenHandler(IClientCredentialsTokenService tokens, DownstreamApiOptions target)
    {
        _tokens = tokens;
        _target = target;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(_target, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}
