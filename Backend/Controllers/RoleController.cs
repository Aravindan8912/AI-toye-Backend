using JarvisBackend.Models;
using JarvisBackend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace JarvisBackend.Controllers;

[ApiController]
[Route("api/roles")]
public class RoleController : ControllerBase
{
    private readonly IRoleService _roleService;
    private readonly ILogger<RoleController> _logger;

    public RoleController(IRoleService roleService, ILogger<RoleController> logger)
    {
        _roleService = roleService;
        _logger = logger;
    }

    /// <summary>API info. POST to this URL with body to upload a role; GET /api/roles/ironman to fetch one.</summary>
    [HttpGet]
    public IActionResult Info()
    {
        return Ok(new
        {
            message = "Roles API. POST /api/roles with body { role, name?, style?, maxLength? } to upload. GET /api/roles/{id} to get role.",
            upload = "POST /api/roles",
            get = "GET /api/roles/{id} (e.g. /api/roles/ironman)"
        });
    }

    /// <summary>Upload or update a character role. Id is the role key (e.g. ironman, spiderman).</summary>
    [HttpPost]
    public async Task<IActionResult> Upload([FromBody] RoleUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Role))
            return BadRequest(new { error = "role is required." });

        var key = request.Role.Trim().ToLowerInvariant();
        var role = new Role
        {
            Id = key,
            RoleKey = key,
            Name = request.Name?.Trim() ?? request.Role,
            Style = request.Style?.Trim() ?? "",
            MaxLength = request.MaxLength?.Trim() ?? "short"
        };

        await _roleService.SaveAsync(role);
        return Ok(new { message = "Role saved.", id = role.Id, name = role.Name });
    }

    /// <summary>Get a role by id (e.g. ironman).</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var role = await _roleService.GetRoleAsync(id);
        if (role == null)
            return NotFound(new { error = "Role not found.", id });
        return Ok(new { id = role.Id, role = role.RoleKey, name = role.Name, style = role.Style, maxLength = role.MaxLength });
    }
}

/// <summary>Request body for POST /api/roles</summary>
public class RoleUploadRequest
{
    public string Role { get; set; } = "";
    public string? Name { get; set; }
    public string? Style { get; set; }
    public string? MaxLength { get; set; }
}
