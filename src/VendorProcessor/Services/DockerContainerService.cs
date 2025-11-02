using System.ComponentModel;
using Docker.DotNet;
using Docker.DotNet.Models;
using RabbitMQ.Client;

namespace VendorProcessor.Services
{
    public class DockerContainerService
    {
        private readonly IDockerClient _dockerClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DockerContainerService> _logger;

        public DockerContainerService(IDockerClient dockerClient, ILogger<DockerContainerService> logger, IConfiguration configuration)
        {
            _dockerClient = dockerClient;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task DeployContainerAsync(string tagname, string name)
        {
            var imageName = tagname.Replace(":stores", string.Empty);
            _logger.LogInformation("Attempting to deploy container {Name} from image {Image}...", name, imageName);

            try
            {
                // Pull the Docker image if it doesn't exist locally

                Dictionary<string, string> mapEnv = new Dictionary<string, string>();
                var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters 
                    { Filters = new Dictionary<string, IDictionary<string, bool>> { { "reference", new Dictionary<string, bool> { { tagname, true } } } } });
                if (!images.Any())
                {
                     await Task.CompletedTask;
                }
                var firstImage = images.First();
                var imageDetails = await _dockerClient.Images.InspectImageAsync(firstImage.ID);
                var envVars = imageDetails.Config.Env?.ToList();
                if (envVars != null)
                {
                    foreach (var envVar in envVars)
                    {
                        var keyVal = envVar.Split('=');
                        mapEnv.Add(keyVal[0], keyVal[1]);
                    }
                }

                //port binding
                // Define a map of ports that should be exposed in the container.
                var exposedPorts = new Dictionary<string, EmptyStruct>
                {
                    // Expose HTTP port 80 inside the container.
                    { "80/tcp", default(EmptyStruct) },
                    // Expose HTTPS port 443 inside the container.
                    { "443/tcp", default(EmptyStruct) }
                };

                var httpPort = string.Empty;
                var httpsPort = string.Empty;
                var siteDomain = string.Empty;

                mapEnv.TryGetValue("HTTP_PORT", out httpPort);
                mapEnv.TryGetValue("HTTPS_PORT", out httpsPort);
                mapEnv.TryGetValue("SITE_DOMAIN", out siteDomain);
                // Define the port mappings from the host to the container.
                var portBindings = new Dictionary<string, IList<PortBinding>>
                {
                    // Map HTTP host port 8080 to container port 80.
                    { "80/tcp", new List<PortBinding> { new PortBinding { HostPort = httpPort } } },
                    // Map HTTPS host port 8443 to container port 443.
                    { "443/tcp", new List<PortBinding> { new PortBinding { HostPort = httpsPort } } }
                };
                //retrieve connection strings and endpoints of other services

                var rabbitMqConnectionString = _configuration.GetConnectionString("ExternalRabbitMQ");
                var basketApiEndpoint = _configuration["Services:basket-api:http:0"];
                var catalogApiEndpoint = _configuration["Services:catalog-api:http:0"];
                var orderingApiEndpoint = _configuration["Services:ordering-api:http:0"];
                var identityUrl = _configuration["IdentityUrl"];

                const string EnvVarName = "ESHOP_USE_HTTP_ENDPOINTS";
                var envValue = Environment.GetEnvironmentVariable(EnvVarName);
                var callbackUrl = string.Empty;

                var httpRunning = int.TryParse(envValue, out int result) && result == 1;
                if (httpRunning)
                {
                    if (string.IsNullOrEmpty(siteDomain))
                    {
                        callbackUrl = $"http://localhost:{httpPort}";
                    }
                    else
                    {
                        callbackUrl = $"http://{siteDomain}";
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(siteDomain))
                    {
                        callbackUrl = $"https://localhost:{httpsPort}";
                    }
                    else
                    {
                        callbackUrl = $"https://{siteDomain}";
                    }
                }
                
                var imageEnvVars = new Dictionary<string, string>
                {
                    { "ConnectionStrings__ExternalRabbitMQ", !string.IsNullOrEmpty(rabbitMqConnectionString) ? rabbitMqConnectionString : string.Empty },
                    { "Services__basket-api__http__0", !string.IsNullOrEmpty(basketApiEndpoint) ? basketApiEndpoint.ToString() : string.Empty },
                    { "Services__catalog-api__http__0", !string.IsNullOrEmpty(catalogApiEndpoint) ? catalogApiEndpoint.ToString() : string.Empty },
                    { "Services__ordering-api__http__0", !string.IsNullOrEmpty(orderingApiEndpoint) ? orderingApiEndpoint.ToString() : string.Empty },
                    { "IdentityUrl", !string.IsNullOrEmpty(identityUrl) ? identityUrl.ToString() : string.Empty},
                    { "CallBackUrl", callbackUrl }
                };
                //Cert for local host
                var certPath = Path.Combine(Directory.GetCurrentDirectory(), "./certs/");
                var containerPath = "/https/";

                // Create the container
                var createResponse = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
                {
                    Image = tagname,
                    Cmd = new List<string> { "cat", containerPath },
                    Name = name,
                    ExposedPorts = exposedPorts,
                    Env = imageEnvVars.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                    HostConfig = new HostConfig
                    {
                        PortBindings = portBindings,
                        Binds =
                        [
                            $"{certPath}:{containerPath}" // Host path:Container path
                        ]
                    }
                });


                // Start the container
                await _dockerClient.Containers.StartContainerAsync(createResponse.ID, new ContainerStartParameters());

                _logger.LogInformation("Successfully deployed container {Name} with ID {ID}", name, createResponse.ID);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deploy container {Name}.", name);
                throw;
            }
        }
    
    }
}
