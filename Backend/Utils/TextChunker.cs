namespace JarvisBackend.Utils;

/// <summary>
/// Splits long knowledge text into embedding-sized chunks. Target ~200–400 tokens per chunk
/// (English ≈ 4 characters/token → default 1200 chars ≈ 300 tokens). Prefers paragraph, then sentence, then word boundaries.
/// </summary>
public static class TextChunker
{
    /// <param name="text">Full document text.</param>
    /// <param name="maxChars">Upper bound per chunk in characters (~4 chars ≈ 1 token for English).</param>
    public static List<string> Chunk(string text, int maxChars = 1200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        text = text.Trim();
        if (text.Length <= maxChars)
            return new List<string> { text };

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= maxChars)
            {
                var tail = text[start..].Trim();
                if (tail.Length > 0)
                    chunks.Add(tail);
                break;
            }

            var splitEnd = FindSplitEnd(text, start, maxChars);
            if (splitEnd <= start)
                splitEnd = Math.Min(start + maxChars, text.Length);

            var piece = text.Substring(start, splitEnd - start).Trim();
            if (piece.Length > 0)
                chunks.Add(piece);

            start = splitEnd;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        return chunks;
    }

    private static int FindSplitEnd(string text, int start, int maxChars)
    {
        var len = Math.Min(maxChars, text.Length - start);
        if (len <= 0)
            return start;

        var segment = text.Substring(start, len);
        var minSplit = Math.Max(32, len / 5);

        var p = segment.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (p >= minSplit)
            return start + p + 2;

        var sentence = segment.LastIndexOf(". ", StringComparison.Ordinal);
        if (sentence >= minSplit)
            return start + sentence + 2;

        var n = segment.LastIndexOf('\n');
        if (n >= minSplit)
            return start + n + 1;

        var sp = segment.LastIndexOf(' ');
        if (sp >= minSplit)
            return start + sp + 1;

        return start + len;
    }
}
