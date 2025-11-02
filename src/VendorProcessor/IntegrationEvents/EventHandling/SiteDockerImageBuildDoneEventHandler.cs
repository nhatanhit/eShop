using eShop.EventBus.Abstractions;
using VendorProcessor.IntegrationEvents.Events;
using VendorProcessor.Services;

namespace eShop.VendorProcessor.IntegrationEvents.EventHandling;
public class SiteDockerImageBuildDoneEventHandler : IIntegrationEventHandler<SiteDockerImageBuildDoneIntegrationEvent>
{
    private readonly ILogger<SiteDockerImageBuildDoneEventHandler> _logger;
    private readonly DockerContainerService _dockerContainerService;

    public SiteDockerImageBuildDoneEventHandler(ILogger<SiteDockerImageBuildDoneEventHandler> logger, DockerContainerService dockerContainerService) {
        _logger = logger;
        _dockerContainerService = dockerContainerService;
    }
    public async Task Handle(SiteDockerImageBuildDoneIntegrationEvent @event)
    {
        Guid g = Guid.NewGuid();
        _logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);
        string vendorDockerImageTag = @event.DockerTag;
        string containerName = vendorDockerImageTag.Replace(":", "_") + $"_{g}";
        if (vendorDockerImageTag != null)
        {
            await _dockerContainerService.DeployContainerAsync(vendorDockerImageTag, containerName);
        }
       
    }
}
