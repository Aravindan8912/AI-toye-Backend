using JarvisBackend.Models;
using MongoDB.Driver;

namespace JarvisBackend.Data;

/// <summary>MongoDB connection and collection access for chat memory and knowledge (vector search).</summary>
public class MongoService
{
    private readonly IMongoCollection<ChatMemory> _memoryCollection;
    private readonly IMongoCollection<Knowledge> _knowledgeCollection;
    private readonly IMongoCollection<Role> _roleCollection;
    private readonly ILogger<MongoService> _logger;

    public MongoService(IConfiguration config, ILogger<MongoService> logger)
    {
        _logger = logger;
        var connectionString = config["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
        var databaseName = config["MongoDB:DatabaseName"] ?? "jarvis";
        var memoryName = config["MongoDB:MemoryCollectionName"] ?? "memory";
        var knowledgeName = config["MongoDB:KnowledgeCollectionName"] ?? "knowledge";
        var roleCollectionName = config["MongoDB:RoleCollectionName"] ?? "roles";

        var client = new MongoClient(connectionString);
        var db = client.GetDatabase(databaseName);
        _memoryCollection = db.GetCollection<ChatMemory>(memoryName);
        _knowledgeCollection = db.GetCollection<Knowledge>(knowledgeName);
        _roleCollection = db.GetCollection<Role>(roleCollectionName);
        _logger.LogInformation("MongoDB: connected to {Database}.{Memory}, {Knowledge}, {Roles}", databaseName, memoryName, knowledgeName, roleCollectionName);
    }

    public IMongoCollection<ChatMemory> Collection => _memoryCollection;
    public IMongoCollection<Knowledge> KnowledgeCollection => _knowledgeCollection;
    public IMongoCollection<Role> RoleCollection => _roleCollection;
}