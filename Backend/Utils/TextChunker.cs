public static class TextChunker
{
    public static List<string> Chunk(string text, int size = 300)
    {
        var chunks = new List<string>();

        for (int i = 0; i < text.Length; i += size)
        {
            chunks.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        }

        return chunks;
    }
}