using JarvisBackend.Models;
using MongoDB.Driver;

namespace JarvisBackend.Data;

/// <summary>MongoDB connection and collection access for chat memory (and optional vector search).</summary>
public class MongoService
{
    private readonly IMongoCollection<ChatMemory> _collection;
    private readonly ILogger<MongoService> _logger;

    public MongoService(IConfiguration config, ILogger<MongoService> logger)
    {
        _logger = logger;
        var connectionString = config["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
        var databaseName = config["MongoDB:DatabaseName"] ?? "jarvis";
        var collectionName = config["MongoDB:MemoryCollectionName"] ?? "memory";

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(databaseName);
        _collection = db.GetCollection<ChatMemory>(collectionName);
        _logger.LogInformation("MongoDB: connected to {Database}.{Collection}", databaseName, collectionName);
    }

    public IMongoCollection<ChatMemory> Collection => _collection;
}