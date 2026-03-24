using System.Text;
using JarvisBackend.Models;
using JarvisBackend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace JarvisBackend.Controllers;

[ApiController]
[Route("api/knowledge")]
public class KnowledgeController : ControllerBase
{
    private readonly IKnowledgeService _knowledge;
    private readonly IMemoryService _memory;
    private readonly IEmbeddingService _embed;
    private readonly IOllamaService _ollama;
    private readonly IConfiguration _config;
    private readonly ILogger<KnowledgeController> _logger;

    public KnowledgeController(
        IKnowledgeService knowledge,
        IMemoryService memory,
        IEmbeddingService embed,
        IOllamaService ollama,
        IConfiguration config,
        ILogger<KnowledgeController> logger)
    {
        _knowledge = knowledge;
        _memory = memory;
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

    /// <summary>Ask a question; knowledge + prior conversation for this ClientId are used. Turn is saved to memory.</summary>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Question))
            return BadRequest(new { error = "Question is required." });

        try
        {
            var question = request.Question.Trim();
            var clientId = ResolveClientId(request.ClientId);
            var embedding = await _embed.GetEmbedding(question);
            var (prompt, retrievalCount) = await BuildKnowledgePromptAsync(question, clientId, embedding);

            var selectedModel = request.Model ?? _config["Knowledge:Model"] ?? _config["Ollama:Model"];
            var maxTokens = request.MaxTokens ?? _config.GetValue("Knowledge:NumPredict", _config.GetValue("Ollama:NumPredict", 50));
            var answer = await _ollama.GenerateAsync(prompt, selectedModel, maxTokens, HttpContext.RequestAborted);
            await SaveConversationTurnAsync(question, answer ?? "", embedding, clientId);
            _logger.LogInformation("Knowledge ask: model {Model}, question length {QL}, chunks {N}, answer length {AL}, client {ClientId}", selectedModel, question.Length, retrievalCount, answer?.Length ?? 0, clientId);
            return Ok(new { question = request.Question.Trim(), answer = answer ?? "", clientId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Knowledge ask failed");
            return StatusCode(503, new { error = "Ollama or embedding service unavailable. Start Ollama (e.g. ollama run llama3, ollama run nomic-embed-text)." });
        }
    }

    [HttpPost("ask/stream")]
    public async Task AskStream([FromBody] AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Question))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = "Question is required." });
            return;
        }

        try
        {
            var question = request.Question.Trim();
            var clientId = ResolveClientId(request.ClientId);
            var embedding = await _embed.GetEmbedding(question);
            var (prompt, retrievalCount) = await BuildKnowledgePromptAsync(question, clientId, embedding);
            var selectedModel = request.Model ?? _config["Knowledge:Model"] ?? _config["Ollama:Model"];
            var maxTokens = request.MaxTokens ?? _config.GetValue("Knowledge:NumPredict", _config.GetValue("Ollama:NumPredict", 50));

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";

            var answerBuilder = new StringBuilder();
            await foreach (var token in _ollama.GenerateStreamAsync(prompt, selectedModel, maxTokens, HttpContext.RequestAborted))
            {
                answerBuilder.Append(token);
                var safeToken = token.Replace("\r", " ").Replace("\n", " ");
                await Response.WriteAsync($"data: {safeToken}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }

            await Response.WriteAsync("event: done\ndata: [DONE]\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
            await SaveConversationTurnAsync(question, answerBuilder.ToString(), embedding, clientId);
            _logger.LogInformation("Knowledge ask stream completed: model {Model}, question length {QL}, chunks {N}, client {ClientId}", selectedModel, question.Length, retrievalCount, clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Knowledge ask stream failed");
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await Response.WriteAsJsonAsync(new { error = "Ollama or embedding service unavailable. Start Ollama and retry." });
            }
        }
    }

    private static string ResolveClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return "web";
        return clientId.Trim();
    }

    private async Task SaveConversationTurnAsync(string question, string answer, float[] embedding, string clientId)
    {
        try
        {
            await _memory.Save(new ChatMemory
            {
                Id = Guid.NewGuid().ToString(),
                UserText = question,
                BotText = answer,
                Embedding = embedding,
                Timestamp = DateTime.UtcNow,
                ClientId = clientId
            }, clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save conversation to memory for client {ClientId}", clientId);
        }
    }

    private async Task<(string Prompt, int ChunkCount)> BuildKnowledgePromptAsync(string question, string clientId, float[]? queryEmbedding)
    {
        var emb = queryEmbedding ?? await _embed.GetEmbedding(question);
        var (knowledgeContext, chunkCount) = await _knowledge.BuildKnowledgeContextForQueryAsync(question, emb);

        var kb = string.IsNullOrEmpty(knowledgeContext)
            ? "(No matching knowledge was retrieved from the database.)"
            : knowledgeContext;

        var memoryBlock = await BuildMemoryContextForClientAsync(clientId, emb);
        var merged = string.IsNullOrWhiteSpace(memoryBlock)
            ? kb
            : $"{memoryBlock}\n\n---\n\nKnowledge base:\n{kb}";

        var template = _config["Knowledge:RagPromptTemplate"];
        if (!string.IsNullOrWhiteSpace(template))
        {
            var prompt = template
                .Replace("{context}", merged, StringComparison.Ordinal)
                .Replace("{question}", question, StringComparison.Ordinal);
            return (prompt, chunkCount);
        }

        var promptDefault = $@"You are Spider-Man.

Rules:
- Introduce yourself once.
- Answer clearly using the context.
- Keep it short.
- If the user asks about something said earlier, use the conversation section in the context.

Context:
{merged}

Question:
{question}

Answer:
";

        return (promptDefault, chunkCount);
    }

    private async Task<string> BuildMemoryContextForClientAsync(string clientId, float[] queryEmbedding)
    {
        var recentLimit = _config.GetValue("Memory:RecentTurnsInPrompt", 12);
        var similarTurns = _config.GetValue("Memory:SimilarTurnsInPrompt", 3);
        var recent = await _memory.GetRecentByClient(clientId, Math.Max(recentLimit + 5, 25));
        var similar = await _memory.Search(queryEmbedding, clientId);

        var recentLines = recent.TakeLast(recentLimit).Select(m => $"U:{m.UserText}\nB:{m.BotText}").ToList();
        var similarSlice = similar.Take(similarTurns).ToList();

        if (recentLines.Count == 0 && similarSlice.Count == 0)
            return "";

        var sb = new StringBuilder();
        if (recentLines.Count > 0)
        {
            sb.AppendLine("Recent conversation (same client / session):");
            sb.AppendLine(string.Join("\n", recentLines));
        }

        if (similarSlice.Count > 0)
        {
            if (recentLines.Count > 0)
                sb.AppendLine();
            sb.AppendLine("Similar past turns:");
            sb.AppendLine(string.Join("\n\n", similarSlice.Select(m => $"U:{m.UserText}\nB:{m.BotText}")));
        }

        return sb.ToString().Trim();
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
    /// <summary>Optional stable id (e.g. browser UUID) so /ask remembers prior turns. Defaults to "web".</summary>
    public string? ClientId { get; set; }
    public string? Model { get; set; }
    public int? MaxTokens { get; set; }
}
