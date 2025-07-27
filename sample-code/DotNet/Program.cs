using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Serilog;
using Serilog.Events;
using System.Collections.Concurrent;
using Microsoft.Graph;
using System.Net.Http;

// Example storage connection service interface and implementation
public interface IStorageConnectionService
{
    string GetConnectionString();
}

public class StorageConnectionService : IStorageConnectionService
{
    private readonly IConfiguration _configuration;

    public StorageConnectionService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GetConnectionString()
    {
        // Assumes "AzureWebJobsStorage" is the key in configuration
        return _configuration.GetConnectionString("AzureWebJobsStorage") 
            ?? _configuration["AzureWebJobsStorage"];
    }
}

// Cosmos connection service interface and implementation
public interface ICosmosConnectionService
{
    CosmosClient GetCosmosClient();
    Database GetDatabase(string databaseName);
    void RegisterDatabase(string databaseName, string? cosmosUrl = null, string? cosmosKey = null);
}

public class CosmosConnectionService : ICosmosConnectionService
{
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, Database> _databases = new();
    private CosmosClient _defaultCosmosClient;
    private string _defaultCosmosUrl;
    private string _defaultCosmosKey;
    private CosmosClientOptions _gatewayOptions;

    public CosmosConnectionService(IConfiguration configuration)
    {
        _configuration = configuration;

        _defaultCosmosUrl = _configuration["Cosmos:Url"];
        _defaultCosmosKey = _configuration["Cosmos:Key"];

        // Configure CosmosClientOptions to use Gateway mode
        _gatewayOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway
            // You can fine-tune other options here as needed
        };

        if (!string.IsNullOrEmpty(_defaultCosmosKey))
        {
            // Use key authentication with Gateway mode
            _defaultCosmosClient = new CosmosClient(_defaultCosmosUrl, _defaultCosmosKey, _gatewayOptions);
        }
        else
        {
            // Use Azure Identity (DefaultAzureCredential) with Gateway mode
            _defaultCosmosClient = new CosmosClient(_defaultCosmosUrl, new DefaultAzureCredential(), _gatewayOptions);
        }
    }

    public CosmosClient GetCosmosClient()
    {
        return _defaultCosmosClient;
    }

    public Database GetDatabase(string databaseName)
    {
        // Try to get from cache
        if (_databases.TryGetValue(databaseName, out var db))
        {
            return db;
        }

        // Use default client and register if not present
        var database = _defaultCosmosClient.GetDatabase(databaseName);
        _databases.TryAdd(databaseName, database);
        return database;
    }

    /// <summary>
    /// Register a database with a specific Cosmos Url and Key, or use default if not provided.
    /// This allows handling multiple Cosmos DB accounts or databases.
    /// Always uses Gateway connection mode.
    /// </summary>
    public void RegisterDatabase(string databaseName, string? cosmosUrl = null, string? cosmosKey = null)
    {
        if (_databases.ContainsKey(databaseName))
            return;

        CosmosClient client;
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway
        };

        if (!string.IsNullOrEmpty(cosmosUrl))
        {
            if (!string.IsNullOrEmpty(cosmosKey))
            {
                client = new CosmosClient(cosmosUrl, cosmosKey, options);
            }
            else
            {
                client = new CosmosClient(cosmosUrl, new DefaultAzureCredential(), options);
            }
        }
        else
        {
            client = _defaultCosmosClient;
        }

        var database = client.GetDatabase(databaseName);
        _databases.TryAdd(databaseName, database);
    }
}

// Graph API service interface and implementation
public interface IGraphApiService
{
    GraphServiceClient GetGraphClient();
}

public class GraphApiService : IGraphApiService
{
    private readonly GraphServiceClient _graphClient;

    public GraphApiService(IConfiguration configuration)
    {
        // Use DefaultAzureCredential for authentication
        var credential = new DefaultAzureCredential();

        // Optionally, you can get the tenantId from configuration if needed
        // string tenantId = configuration["AzureAd:TenantId"];

        _graphClient = new GraphServiceClient(credential);
    }

    public GraphServiceClient GetGraphClient()
    {
        return _graphClient;
    }
}

// Configure Serilog with multiple sinks
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/app.log", rollingInterval: RollingInterval.Day)
    // Add more sinks as needed, e.g., Seq, Azure, etc.
    .CreateLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog() // Use Serilog for logging
        .ConfigureAppConfiguration((hostingContext, config) =>
        {
            config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                  .AddEnvironmentVariables();
        })
        .ConfigureServices((hostContext, services) =>
        {
            // Register your worker and dependencies here
            services.AddHostedService<ObsoleteWorker>();

            // Register the storage connection service
            services.AddSingleton<IStorageConnectionService, StorageConnectionService>();

            // Register the Cosmos connection service
            services.AddSingleton<ICosmosConnectionService, CosmosConnectionService>();

            // Register the Graph API service
            services.AddSingleton<IGraphApiService, GraphApiService>();

            // Example: Add other services, e.g., Azure clients, configuration, etc.
            // services.AddSingleton<IMyService, MyService>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}