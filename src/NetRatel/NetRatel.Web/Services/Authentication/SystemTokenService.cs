using System.Net.Http.Json;

namespace NetRatel.Web.Services.Authentication;

public class SystemTokenService : ISystemTokenService
{
    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration _config;
    private string? _token;
    private DateTime _expires;

    public SystemTokenService(IHttpClientFactory factory, IConfiguration config)
    {
        _factory = factory;
        _config = config;
    }

    public async Task<string> GetTokenAsync()
    {
        if (_token != null && DateTime.UtcNow < _expires)
            return _token;

        var client = _factory.CreateClient("SystemApiNoAuth");
        var apiBase = _config["ApiBaseUrl"] ?? "http://localhost:5005/";
        var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBase.TrimEnd('/')}/api/v1/system/token");
        var secret = _config["SystemTokenSecret"] ?? string.Empty;
        request.Headers.Add("X-System-Secret", secret);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<SystemTokenResponse>();

        _token = token?.token ?? string.Empty;
        _expires = DateTime.UtcNow.AddMinutes(9);
        return _token!;
    }

    private sealed record SystemTokenResponse(string token);
}
