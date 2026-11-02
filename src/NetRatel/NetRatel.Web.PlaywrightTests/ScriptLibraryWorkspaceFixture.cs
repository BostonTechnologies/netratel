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
    public UpdateScriptRequest? LastUpdate { get; private set; }
    public void Register(IServiceCollection services) => services.AddSingleton(new ScriptLibraryClient(this));
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://script-fixture.invalid") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
            return new(HttpStatusCode.OK) { Content = ulong.TryParse(request.RequestUri!.Segments.Last(), out var requestedId)
                ? JsonContent.Create(_scripts.Single(x => x.Id == requestedId)) : JsonContent.Create(_scripts) };
        if (request.Method == HttpMethod.Put)
        {
            LastUpdate = (await request.Content!.ReadFromJsonAsync<UpdateScriptRequest>(cancellationToken))!;
            var id = ulong.Parse(request.RequestUri!.Segments.Last());
            var index = _scripts.FindIndex(x => x.Id == id); var saved = _scripts[index];
            if (LastUpdate.ExpectedSourceRevision != saved.SourceRevision)
                return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { remediation = "Reload saved details before retrying." }) };
            _scripts[index] = saved with { FolderPath = LastUpdate.FolderPath ?? saved.FolderPath, Name = LastUpdate.Name ?? saved.Name,
                Content = LastUpdate.Content ?? saved.Content, SourceRevision = saved.SourceRevision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id, sourceRevision = _scripts[index].SourceRevision }) };
        }
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
