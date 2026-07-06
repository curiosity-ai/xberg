using Xberg.Core;
using Xberg.Types;

// Minimal CLI: extract a single file (or a piece of literal text) and print the result.
// The full fixture-diffing runner (Phase 0) is not wired up yet.

if (args.Length == 0)
{
    Console.WriteLine("Xberg TestRunner");
    Console.WriteLine("Usage: xberg-testrunner <file> [--format plain|markdown|html|json|djot]");
    return 0;
}

string path = args[0];
var format = OutputFormat.Plain;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--format")
        format = OutputFormat.FromString(args[i + 1]);
}

var config = new ExtractionConfig { OutputFormat = format };
var extractor = new Extractor();
var result = extractor.Extract(ExtractInput.FromUri(path), config);

foreach (var doc in result.Results)
{
    Console.WriteLine($"# mime: {doc.MimeType}");
    Console.WriteLine(doc.Content);
}
foreach (var err in result.Errors)
{
    Console.Error.WriteLine($"error: {err.Message}");
}
return result.Errors.Count > 0 ? 1 : 0;
