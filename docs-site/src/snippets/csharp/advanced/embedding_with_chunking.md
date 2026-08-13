```csharp title="C#"
using Xberg;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

var config = new ExtractionConfig
{
    Chunking = new ChunkingConfig
    {
        MaxCharacters = 512,
        Overlap = 50,
        Embedding = new EmbeddingConfig
        {
            Model = new EmbeddingModelType.Preset("balanced"),
            Normalize = true,
            BatchSize = 32,
            ShowDownloadProgress = false
        }
    }
};

var result = (await XbergConverter.ExtractAsync(ExtractInput.FromUri("document.pdf"), config)).Results[0];

var chunks = result.Chunks ?? new List<Chunk>();
foreach (var (index, chunk) in chunks.WithIndex())
{
    var chunkId = $"doc_chunk_{index}";
    Console.WriteLine($"Chunk {chunkId}: {chunk.Content[..Math.Min(50, chunk.Content.Length)]}");

    if (chunk.Embedding != null)
    {
        Console.WriteLine($"  Embedding dimensions: {chunk.Embedding.Count}");
    }
}

internal static class EnumerableExtensions
{
    public static IEnumerable<(int Index, T Item)> WithIndex<T>(
        this IEnumerable<T> items)
    {
        var index = 0;
        foreach (var item in items)
        {
            yield return (index++, item);
        }
    }
}
```
