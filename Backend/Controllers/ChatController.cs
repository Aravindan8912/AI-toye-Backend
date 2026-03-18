using Microsoft.AspNetCore.Mvc;

namespace JarvisBackend.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly JarvisBackend.Services.Interfaces.IMemoryService _memory;

    public ChatController(JarvisBackend.Services.Interfaces.IMemoryService memory)
    {
        _memory = memory;
    }

    /// <summary>Returns recent conversation turns from MongoDB (what you spoke + AI reply). No embeddings.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int limit = 50)
    {
        var list = await _memory.GetRecent(Math.Clamp(limit, 1, 100));
        var items = list
            .OrderBy(x => x.Timestamp)
            .Select(m => new { userText = m.UserText ?? "", botText = m.BotText ?? "", timestamp = m.Timestamp })
            .ToList();
        return Ok(items);
    }
}
