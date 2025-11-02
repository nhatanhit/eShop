using Docker.DotNet;
using eShop.ServiceDefaults;
using eShop.VendorProcessor.IntegrationEvents.EventHandling;
using VendorProcessor.IntegrationEvents.Events;
using VendorProcessor.Services;

// Determine the Docker host based on the operating system
var dockerUri = OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";



var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddSingleton<IDockerClient>(sp =>
{
    return new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
});

builder.Services.AddSingleton<DockerContainerService>();


builder.AddRabbitMqEventBus("EventBus").AddSubscription<SiteDockerImageBuildDoneIntegrationEvent, SiteDockerImageBuildDoneEventHandler>();
var app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync();
