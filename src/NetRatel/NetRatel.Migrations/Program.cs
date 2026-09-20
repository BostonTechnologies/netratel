using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddNetRatelInfrastructure(configuration);
await using var provider = services.BuildServiceProvider();
Console.WriteLine("Applying NetRatel application and identity migrations.");
await provider.MigrateNetRatelInfrastructureAsync();
Console.WriteLine("NetRatel database migrations completed.");
