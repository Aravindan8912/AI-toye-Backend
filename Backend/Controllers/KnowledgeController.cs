using JarvisBackend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace JarvisBackend.Controllers;

[ApiController]
[Route("api/knowledge")]
public class KnowledgeController : ControllerBase
{
    private readonly IKnowledgeService _knowledge;
    private readonly IEmbeddingService _embed;
    private readonly IOllamaService _ollama;
    private readonly IConfiguration _config;
    private readonly ILogger<KnowledgeController> _logger;

    public KnowledgeController(IKnowledgeService knowledge, IEmbeddingService embed, IOllamaService ollama, IConfiguration config, ILogger<KnowledgeController> logger)
    {
        _knowledge = knowledge;
        _embed = embed;
        _ollama = ollama;
        _config = config;
        _logger = logger;
    }

    /// <summary>Add knowledge (e.g. Spider-Man details). Content is chunked and embedded for semantic search.</summary>
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddKnowledgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Title) || string.IsNullOrWhiteSpace(request?.Content))
            return BadRequest(new { error = "Title and Content are required." });

        try
        {
            await _knowledge.SaveWithEmbeddingAsync(request.Title.Trim(), request.Content.Trim());
            return Ok(new { message = "Knowledge added.", title = request.Title });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add knowledge");
            return StatusCode(500, new { error = "Failed to add knowledge. Ensure Ollama is running with an embedding model (e.g. nomic-embed-text)." });
        }
    }

    /// <summary>List all stored knowledge entries (no embeddings in response).</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var all = await _knowledge.GetAllAsync();
        var items = all.Select(k => new { id = k.Id.ToString(), title = k.Title, contentPreview = k.Content?.Length > 200 ? k.Content.Substring(0, 200) + "..." : k.Content });
        return Ok(new { count = all.Count, items });
    }

    /// <summary>Ask a question; relevant knowledge is retrieved and fed to Ollama so it answers using your data (e.g. Spider-Man).</summary>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Question))
            return BadRequest(new { error = "Question is required." });

        try
        {
            var queryEmbedding = await _embed.GetEmbedding(request.Question.Trim());
            var relevant = await _knowledge.SearchAsync(queryEmbedding);
            var context = relevant.Count > 0
                ? string.Join("\n\n", relevant.Select(k => $"[{k.Title}]\n{k.Content}"))
                : "(No relevant knowledge in the database. Add some via POST /api/knowledge.)";

            var persona = _config["Assistant:Persona"] ?? "";
            var userProfile = _config["Assistant:UserProfile"] ?? "";
            var systemBlock = (string.IsNullOrWhiteSpace(persona) && string.IsNullOrWhiteSpace(userProfile))
                ? ""
                : string.Join("\n\n", new[] { persona, userProfile }.Where(s => !string.IsNullOrWhiteSpace(s))) + "\n\n";
            var prompt = $@"{systemBlock}Answer based on the following knowledge when relevant. If the answer is not in the knowledge, use your character/persona to respond.

Knowledge:
{context}

Question: {request.Question.Trim()}

Answer:";

            var answer = await _ollama.GenerateAsync(prompt);
            _logger.LogInformation("Knowledge ask: question length {QL}, chunks {N}, answer length {AL}", request.Question.Length, relevant.Count, answer?.Length ?? 0);
            return Ok(new { question = request.Question.Trim(), answer = answer ?? "" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Knowledge ask failed");
            return StatusCode(503, new { error = "Ollama or embedding service unavailable. Start Ollama (e.g. ollama run llama3, ollama run nomic-embed-text)." });
        }
    }
}

public class AddKnowledgeRequest
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
}

public class AskRequest
{
    public string Question { get; set; } = "";
}
