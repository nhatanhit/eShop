using eShop.EventBus.Events;

namespace VendorProcessor.IntegrationEvents.Events;

public record SiteDockerImageBuildDoneIntegrationEvent(string RootStoreProject, string DockerTag, string ProjectName, string DockerFileWorkingDirectory,bool NoCache, string Target, string Platform) : IntegrationEvent;


