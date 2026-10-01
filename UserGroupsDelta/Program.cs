using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using UserGroupsDelta.GroupsDelta;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddOptions<GroupsDeltaOptions>()
    .BindConfiguration(GroupsDeltaOptions.SectionName)
    .Validate(
        options => Uri.TryCreate(options.GraphBaseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps,
        "GroupsDelta:GraphBaseUrl must be an absolute HTTPS URL.")
    .Validate(options => options.ResponseLimit >= 0, "GroupsDelta:ResponseLimit cannot be negative.")
    .Validate(options => options.MaxRetries >= 0, "GroupsDelta:MaxRetries cannot be negative.")
    .ValidateOnStart();

builder.Services.AddSingleton<TokenCredential>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var tenantId = GetRequiredSetting(configuration, "AzureTenantId");
    var clientId = GetRequiredSetting(configuration, "AzureClientId");
    var clientSecret = GetRequiredSetting(configuration, "AzureClientSecret");
    return new ClientSecretCredential(tenantId, clientId, clientSecret);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var storageServiceUri = configuration["GroupsDeltaStorageServiceUri"];

    if (Uri.TryCreate(storageServiceUri, UriKind.Absolute, out var serviceUri))
    {
        return new BlobServiceClient(
            serviceUri,
            serviceProvider.GetRequiredService<TokenCredential>());
    }

    var connectionString = configuration["GroupsDeltaStorage"]
        ?? configuration["AzureWebJobsStorage"]
        ?? throw new InvalidOperationException(
            "Configure GroupsDeltaStorageServiceUri, GroupsDeltaStorage, or AzureWebJobsStorage.");
    return new BlobServiceClient(connectionString);
});
builder.Services.AddHttpClient<IGroupsDeltaClient, GroupsDeltaClient>();
builder.Services.AddSingleton<IGroupsDeltaCheckpointStore, BlobGroupsDeltaCheckpointStore>();
builder.Services.AddSingleton<IGroupsDeltaSyncService, GroupsDeltaSyncService>();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

await builder.Build().RunAsync();

static string GetRequiredSetting(IConfiguration configuration, string settingName)
{
    var value = configuration[settingName];

    if (string.IsNullOrWhiteSpace(value) || value.StartsWith('<'))
        throw new InvalidOperationException($"Configure the {settingName} application setting.");

    return value;
}
