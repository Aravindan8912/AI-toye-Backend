using JarvisBackend.Models;

namespace JarvisBackend.Utils;

/// <summary>Builds the LLM prompt from role in DB (Name, Style, MaxLength) or fallback to default.</summary>
public static class PromptBuilder
{
    /// <summary>Build prompt from role document in MongoDB. If roleData is null, uses default assistant.</summary>
    public static string Build(Role? roleData, string userText, string context = "")
    {
        var name = roleData?.Name?.Trim();
        var style = roleData?.Style?.Trim();
        var maxLength = (roleData?.MaxLength ?? "short").Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(name)) name = "Jarvis, a helpful AI assistant";
        if (string.IsNullOrEmpty(style)) style = "brief and clear";

        var lengthRule = maxLength switch
        {
            "short" => "Answer in max 1–2 sentences.",
            "medium" => "Answer in 2–3 sentences.",
            "long" => "You may give a longer answer if needed.",
            _ => "Answer in max 1–2 sentences."
        };

        return $@"You are {name}.

Style: {style}
Rules: {lengthRule} Do NOT greet unless user greets. Never pretend you are physically the character.

{context}

User: {userText}
Answer:
";
    }
}
