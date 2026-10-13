using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Web.Services.ScriptLibrary;

namespace NetRatel.Web.PlaywrightTests;

internal sealed class ScriptLibraryWorkspaceFixture : HttpMessageHandler, IHttpClientFactory
{
    private readonly List<ScriptDto> _scripts = [
        new(11, "Inventory.ps1", "/operations/", "Inventory example", "Write-Output 'inventory'", "PowerShell", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new(12, "Settings.json", "/configuration/", "Stored configuration", "{\"enabled\":true}", "Json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
    ];
    public CreateScriptRequest? LastCreate { get; private set; }
    public void Register(IServiceCollection services) => services.AddSingleton(new ScriptLibraryClient(this));
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://script-fixture.invalid") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(_scripts) };
        if (request.Method == HttpMethod.Post)
        {
            LastCreate = await request.Content!.ReadFromJsonAsync<CreateScriptRequest>(cancellationToken);
            var id = checked((ulong)(_scripts.Count + 20));
            _scripts.Add(new(id, LastCreate!.Name, LastCreate.FolderPath, LastCreate.Description,
                LastCreate.Content, LastCreate.ScriptType, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id }) };
        }
        throw new InvalidOperationException($"Unexpected script fixture request {request.Method} {request.RequestUri}.");
    }
}
