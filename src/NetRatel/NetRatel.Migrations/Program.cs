using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__NetRatelDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings__NetRatelDb is required to apply migrations.");
}

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:NetRatelDb"] = connectionString
    })
    .Build();

var services = new ServiceCollection();
services.AddNetRatelInfrastructure(configuration);
await using var provider = services.BuildServiceProvider();
Console.WriteLine("Applying NetRatel application and identity migrations.");
await provider.MigrateNetRatelInfrastructureAsync();
Console.WriteLine("NetRatel database migrations completed.");
