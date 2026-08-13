```csharp title="C#"
using Xberg;
using System;
using System.Linq;

// NOTE: The C# binding has no standalone "embed arbitrary text" client —
// there is no public EmbedSync/EmbedAsync entry point. Embeddings are only
// produced as part of extraction, attached per chunk, via
// ExtractionConfig.Chunking.Embedding.
var config = new ExtractionConfig
{
    Chunking = new ChunkingConfig
    {
        Embedding = new EmbeddingConfig
        {
            Model = new EmbeddingModelType.Preset("balanced"),
            Normalize = true
        }
    }
};

var result = (await XbergConverter.ExtractAsync(ExtractInput.FromUri("document.pdf"), config)).Results[0];
var chunksWithEmbeddings = result.Chunks?.Where(c => c.Embedding != null).ToList() ?? new();
Console.WriteLine(chunksWithEmbeddings.Count);
Console.WriteLine(chunksWithEmbeddings.FirstOrDefault()?.Embedding?.Count);
```
