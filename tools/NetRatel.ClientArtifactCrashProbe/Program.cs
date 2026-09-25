using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;

if (args.Length != 14)
    throw new ArgumentException("Expected database, storage, operation, claim, artifact, and provenance arguments.");

var connectionString = args[0];
var storageRoot = args[1];
var operationId = Guid.Parse(args[2]);
var leaseOwner = Guid.Parse(args[3]);
var leaseGeneration = long.Parse(args[4]);
var rid = args[5];
var version = args[6];
var archivePath = args[7];
var tag = args[8];
var assetId = long.Parse(args[9]);
var assetName = args[10];
var sourceSha256 = args[11];
var buildCommit = args[12];
var adapterContract = args[13];

var options = Options.Create(new ClientArtifactsOptions
{
    StorageRoot = storageRoot,
    EnableFallbackScan = false,
    TestCommitHook = phase =>
    {
        if (phase == ClientArtifactCommitPhase.AfterAtomicDirectoryMoveBeforeDatabaseCommit)
            Environment.FailFast("crash probe hard-stop after filesystem commit");
    }
});

await using var services = new ServiceCollection()
    .AddLogging()
    .AddDbContext<OrchestratorDbContext>(builder => builder.UseNpgsql(connectionString))
    .AddSingleton<TimeProvider>(TimeProvider.System)
    .AddSingleton<IOptions<ClientArtifactsOptions>>(options)
    .AddSingleton<IWebHostEnvironment>(new ProbeEnvironment(storageRoot))
    .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
    .AddSingleton<IOptions<AgentAuthOptions>>(Options.Create(new AgentAuthOptions()))
    .AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>()
    .AddSingleton<IArtifactZipInjectionService, ZipInjectionService>()
    .AddSingleton<ITenantLookupService>(new ProbeTenantLookup())
    .AddSingleton<IEventRecorder>(new ProbeEventRecorder())
    .AddSingleton<ICorrelationContext>(new ProbeCorrelationContext())
    .AddScoped<IClientArtifactsService, ClientArtifactsService>()
    .BuildServiceProvider();

await using var scope = services.CreateAsyncScope();
var artifacts = scope.ServiceProvider.GetRequiredService<IClientArtifactsService>();
await using var archive = File.OpenRead(archivePath);
var file = new FormFile(archive, 0, archive.Length, "file", Path.GetFileName(archivePath))
{
    Headers = new HeaderDictionary(),
    ContentType = "application/zip"
};
await artifacts.ImportVerifiedAsync(file, rid, version,
    new ClientArtifactImportProvenance(operationId, "BostonTechnologies/netratel", tag, assetId,
        assetName, sourceSha256, buildCommit, adapterContract, leaseOwner, leaseGeneration),
    "crash-probe", CancellationToken.None);

internal sealed class ProbeTenantLookup : ITenantLookupService
{
    public Task<bool> TenantExistsAsync(int tenantId, CancellationToken ct = default) => Task.FromResult(tenantId > 0);
}

internal sealed class ProbeEventRecorder : IEventRecorder
{
    public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class ProbeCorrelationContext : ICorrelationContext
{
    public string? Current => "client-artifact-crash-probe";
    public string GetOrCreate() => Current!;
}

internal sealed class ProbeEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "NetRatel.ClientArtifactCrashProbe";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Development";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
}
