using JarvisBackend.Models;
using JarvisBackend.Services.Interfaces;
using MongoDB.Driver;

namespace JarvisBackend.Data;

/// <summary>Fetches and stores character roles from MongoDB. Used by AudioWorker to build prompts.</summary>
public class RoleService : IRoleService
{
    private readonly MongoService _mongo;
    private readonly ILogger<RoleService> _logger;

    public RoleService(MongoService mongo, ILogger<RoleService> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    public async Task<Role?> GetRoleAsync(string roleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roleId)) return null;
        var key = roleId.Trim().ToLowerInvariant();
        return await _mongo.RoleCollection.Find(r => r.Id == key).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(Role role, CancellationToken cancellationToken = default)
    {
        if (role == null) return;
        var key = (role.RoleKey ?? role.Id ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return;
        role.Id = key;
        role.RoleKey = key;
        await _mongo.RoleCollection.ReplaceOneAsync(
            r => r.Id == key,
            role,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
        _logger.LogInformation("Role saved: Id={Id}, Name={Name}", role.Id, role.Name);
    }
}
