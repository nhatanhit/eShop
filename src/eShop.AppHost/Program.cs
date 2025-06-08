using eShop.AppHost;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Runtime.InteropServices;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddForwardedHeaders();

var redis = builder.AddRedis("redis");
var rabbitMq = builder.AddRabbitMQ("eventbus")
    .WithLifetime(ContainerLifetime.Persistent);
var postgres = builder.AddPostgres("postgres")
    .WithImage("ankane/pgvector")
    .WithImageTag("latest")
    .WithLifetime(ContainerLifetime.Persistent);

var catalogDb = postgres.AddDatabase("catalogdb");
var identityDb = postgres.AddDatabase("identitydb");
var orderDb = postgres.AddDatabase("orderingdb");
var webhooksDb = postgres.AddDatabase("webhooksdb");

var launchProfileName = ShouldUseHttpForEndpoints() ? "http" : "https";

// Services
var identityApi = builder.AddProject<Projects.Identity_API>("identity-api", launchProfileName)
    .WithExternalHttpEndpoints()
    .WithReference(identityDb);

var identityEndpoint = identityApi.GetEndpoint(launchProfileName);

var basketApi = builder.AddProject<Projects.Basket_API>("basket-api")
    .WithReference(redis)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithEnvironment("Identity__Url", identityEndpoint);
redis.WithParentRelationship(basketApi);

var catalogApi = builder.AddProject<Projects.Catalog_API>("catalog-api")
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(catalogDb);

var orderingApi = builder.AddProject<Projects.Ordering_API>("ordering-api")
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(orderDb).WaitFor(orderDb)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Identity__Url", identityEndpoint);

builder.AddProject<Projects.OrderProcessor>("order-processor")
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(orderDb)
    .WaitFor(orderingApi); // wait for the orderingApi to be ready because that contains the EF migrations

builder.AddProject<Projects.PaymentProcessor>("payment-processor")
    .WithReference(rabbitMq).WaitFor(rabbitMq);

var webHooksApi = builder.AddProject<Projects.Webhooks_API>("webhooks-api")
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(webhooksDb)
    .WithEnvironment("Identity__Url", identityEndpoint);

// Reverse proxies
builder.AddProject<Projects.Mobile_Bff_Shopping>("mobile-bff")
    .WithReference(catalogApi)
    .WithReference(orderingApi)
    .WithReference(basketApi)
    .WithReference(identityApi);

// Apps
var webhooksClient = builder.AddProject<Projects.WebhookClient>("webhooksclient", launchProfileName)
    .WithReference(webHooksApi)
    .WithEnvironment("IdentityUrl", identityEndpoint);

var webApp = builder.AddProject<Projects.WebApp>("webapp", launchProfileName)
    .WithExternalHttpEndpoints()
    .WithUrls(c => c.Urls.ForEach(u => u.DisplayText = $"Online Store ({u.Endpoint?.EndpointName})"))
    .WithReference(basketApi)
    .WithReference(catalogApi)
    .WithReference(orderingApi)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithEnvironment("IdentityUrl", identityEndpoint);

// set to true if you want to use OpenAI
bool useOpenAI = false;
if (useOpenAI)
{
    builder.AddOpenAI(catalogApi, webApp);
}

bool useOllama = false;
if (useOllama)
{
    builder.AddOllama(catalogApi, webApp);
}

// Wire up the callback urls (self referencing)
webApp.WithEnvironment("CallBackUrl", webApp.GetEndpoint(launchProfileName));
webhooksClient.WithEnvironment("CallBackUrl", webhooksClient.GetEndpoint(launchProfileName));

// Identity has a reference to all of the apps for callback urls, this is a cyclic reference
identityApi.WithEnvironment("BasketApiClient", basketApi.GetEndpoint("http"))
           .WithEnvironment("OrderingApiClient", orderingApi.GetEndpoint("http"))
           .WithEnvironment("WebhooksApiClient", webHooksApi.GetEndpoint("http"))
           .WithEnvironment("WebhooksWebClient", webhooksClient.GetEndpoint(launchProfileName))
           .WithEnvironment("WebAppClient", webApp.GetEndpoint(launchProfileName));

var myDict = new Dictionary<string, string>
        {
            { "Linux", "unix:///var/run/docker.sock" },
            { "Windows", "npipe://./pipe/docker_engine" },
            { "macOS", "unix:///var/run/docker.sock" },
            { "Unknown", "unix:///var/run/docker.sock"}
        };

var os = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
       : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
       : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
       : "Unknown";


var dockerEngineString = string.Empty;
myDict.TryGetValue(os,out dockerEngineString);
if (!string.IsNullOrEmpty(dockerEngineString)) {
    var client = new DockerClientConfiguration(new Uri(dockerEngineString))
        .CreateClient();
    var dockerImages = GetDockerImagesByTagAsync(client, "stores").GetAwaiter().GetResult();
    var certPath = Path.Combine(Directory.GetCurrentDirectory(), "./certs/aspnet-dev.pfx");
    foreach (var image in dockerImages)
    {
        //get environment variable for each docker image
        var imageEnvs = GetImageDetailsAsync(client, image).GetAwaiter().GetResult();
        var imageName = image.RepoTags.FirstOrDefault();
        if (imageName != null)
        {
            var containerName = imageName.Split(':')[0];
            var httpPort = string.Empty;
            var httpsPort = string.Empty;
            imageEnvs.TryGetValue("HTTP_PORT", out httpPort);
            imageEnvs.TryGetValue("HTTPS_PORT", out httpsPort);
            if (!string.IsNullOrEmpty(httpsPort) && !string.IsNullOrEmpty(httpPort))
            {
                var projectRef = builder.AddContainer(containerName, imageName)
                .WithEndpoint(Int32.Parse(httpPort), targetPort: Int32.Parse(httpPort), scheme: "http")
                .WithEndpoint(Int32.Parse(httpsPort), targetPort: Int32.Parse(httpsPort), scheme: "https")
                    .WithExternalHttpEndpoints()
                    .WithReference(basketApi)
                    .WithReference(catalogApi)
                    .WithReference(orderingApi)
                    .WithReference(rabbitMq).WaitFor(rabbitMq)
                    .WithEnvironment("IdentityUrl", identityEndpoint)
                    .WithBindMount(source: certPath, target: "/https/aspnet-dev.pfx");
                projectRef.WithEnvironment("CallBackUrl", projectRef.GetEndpoint(launchProfileName));
                identityApi.WithEnvironment("WebAppClient", projectRef.GetEndpoint(launchProfileName));
            }


        }


    }
}


builder.Build().Run();

// For test use only.
// Looks for an environment variable that forces the use of HTTP for all the endpoints. We
// are doing this for ease of running the Playwright tests in CI.
static bool ShouldUseHttpForEndpoints()
{
    const string EnvVarName = "ESHOP_USE_HTTP_ENDPOINTS";
    var envValue = Environment.GetEnvironmentVariable(EnvVarName);

    // Attempt to parse the environment variable value; return true if it's exactly "1".
    return int.TryParse(envValue, out int result) && result == 1;
}


async Task<List<ImagesListResponse>> GetDockerImagesByTagAsync(DockerClient client, string tag)
{


    var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = true });
    List<ImagesListResponse> list = new List<ImagesListResponse>();

    foreach (var image in images)
    {
        var imageTags = image.RepoTags.ToList();
        if (!string.IsNullOrEmpty(imageTags.FirstOrDefault(t => t.EndsWith($":{tag}", StringComparison.OrdinalIgnoreCase))))
        {
            list.Add(image);
        }
    }

    return list;
}
async Task<Dictionary<string, string>> GetImageDetailsAsync(DockerClient client, ImagesListResponse imagesListResponse)
{
    var imageId = imagesListResponse.ID;
    Dictionary<string, string> mapEnv = new Dictionary<string, string>();
    if (imageId == null)
    {
        return mapEnv;
    }

    var imageDetails = await client.Images.InspectImageAsync(imageId);
    if (imageDetails.Config.Env == null)
    {
        return mapEnv;
    }
    var envVars = imageDetails.Config.Env?.ToList();
    if (envVars != null)
    {
        foreach (var envVar in envVars)
        {
            var keyVal = envVar.Split('=');
            mapEnv.Add(keyVal[0], keyVal[1]);
        }
    }

    return mapEnv;
}
