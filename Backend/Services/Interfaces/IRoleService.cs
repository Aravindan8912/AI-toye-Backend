using JarvisBackend.Models;

namespace JarvisBackend.Services.Interfaces;

/// <summary>Fetches and stores character roles from MongoDB for prompt building.</summary>
public interface IRoleService
{
    Task<Role?> GetRoleAsync(string roleId, CancellationToken cancellationToken = default);
    Task SaveAsync(Role role, CancellationToken cancellationToken = default);
}
