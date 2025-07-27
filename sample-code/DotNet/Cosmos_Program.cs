using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// Interface for Cosmos DB abstraction
public interface ICosmosDbService
{
    Database GetDatabase(string databaseName);
    Container GetContainer(string databaseName, string containerName);

    Task<IEnumerable<T>> QueryItemsAsync<T>(string databaseName, string containerName, string query);
    Task<T> InsertItemAsync<T>(string databaseName, string containerName, T item, string partitionKeyValue);
    Task<T> UpdateItemAsync<T>(string databaseName, string containerName, string id, T item, string partitionKeyValue);
    Task DeleteItemAsync(string databaseName, string containerName, string id, string partitionKeyValue);
}

// Implementation of the Cosmos DB service
public class CosmosDbService : ICosmosDbService
{
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, Database> _databases = new();
    private readonly ConcurrentDictionary<(string, string), Container> _containers = new();
    private readonly CosmosClient _cosmosClient;

    public CosmosDbService(IConfiguration configuration)
    {
        _configuration = configuration;
        var cosmosUrl = _configuration["Cosmos:Url"];
        var cosmosKey = _configuration["Cosmos:Key"];

        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway
        };

        if (!string.IsNullOrEmpty(cosmosKey))
        {
            _cosmosClient = new CosmosClient(cosmosUrl, cosmosKey, options);
        }
        else
        {
            _cosmosClient = new CosmosClient(cosmosUrl, new Azure.Identity.DefaultAzureCredential(), options);
        }
    }

    public Database GetDatabase(string databaseName)
    {
        if (_databases.TryGetValue(databaseName, out var db))
        {
            return db;
        }
        var database = _cosmosClient.GetDatabase(databaseName);
        _databases.TryAdd(databaseName, database);
        return database;
    }

    public Container GetContainer(string databaseName, string containerName)
    {
        var key = (databaseName, containerName);
        if (_containers.TryGetValue(key, out var container))
        {
            return container;
        }
        var db = GetDatabase(databaseName);
        container = db.GetContainer(containerName);
        _containers.TryAdd(key, container);
        return container;
    }

    public async Task<IEnumerable<T>> QueryItemsAsync<T>(string databaseName, string containerName, string query)
    {
        var container = GetContainer(databaseName, containerName);
        var queryDef = new QueryDefinition(query);
        var iterator = container.GetItemQueryIterator<T>(queryDef);
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<T> InsertItemAsync<T>(string databaseName, string containerName, T item, string partitionKeyValue)
    {
        var container = GetContainer(databaseName, containerName);
        var response = await container.CreateItemAsync(item, new PartitionKey(partitionKeyValue));
        return response.Resource;
    }

    public async Task<T> UpdateItemAsync<T>(string databaseName, string containerName, string id, T item, string partitionKeyValue)
    {
        var container = GetContainer(databaseName, containerName);
        var response = await container.ReplaceItemAsync(item, id, new PartitionKey(partitionKeyValue));
        return response.Resource;
    }

    public async Task DeleteItemAsync(string databaseName, string containerName, string id, string partitionKeyValue)
    {
        var container = GetContainer(databaseName, containerName);
        await container.DeleteItemAsync<object>(id, new PartitionKey(partitionKeyValue));
    }
}

// Extension method for DI registration
public static class CosmosDbServiceCollectionExtensions
{
    public static IServiceCollection AddCosmosDbService(this IServiceCollection services)
    {
        services.AddSingleton<ICosmosDbService, CosmosDbService>();
        return services;
    }
}

