var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.NetRatel_API>("netratel-api")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.NetRatel_Web>("netratel-web")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("ApiBaseUrl", api.GetEndpoint("https"))
    .WithEnvironment("NetRatelApi__BaseUrl", api.GetEndpoint("https"))
    .WithEnvironment("ReverseProxy__Clusters__apiCluster__Destinations__api1__Address", api.GetEndpoint("https"));

builder.Build().Run();
