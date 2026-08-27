// Prove an installed xberg-ffi native library actually extracts documents,
// not just that .NET can resolve the P/Invoke import.
//
// [DllImport("xberg_ffi")] entry points are bound lazily: the CLR only
// resolves and loads the native library the first time a specific extern
// method is actually called, so merely starting this process and referencing
// the Xberg assembly proves nothing about the native library at all. This
// program therefore makes a real extraction call against a known fixture and
// asserts the exact expected text comes back -- which forces the native
// library to load, forces its own shared-lib dependency closure (ONNX
// Runtime, libheif, ...) to resolve, and proves the FFI call boundary itself
// works end to end.
//
// Usage: dotnet exec Smoke.dll <fixture-path> <expected-substring>
// Exit code is non-zero, with a message on stderr, on any failure: native
// library load failure, ExtractAsync throwing, an empty result, or a content
// mismatch.
using System;
using System.Threading.Tasks;
using Xberg;

internal static class Smoke {
    private static int Fail(string message) {
        Console.Error.WriteLine($"SMOKE TEST FAILED: {message}");
        return 1;
    }

    private static async Task<int> Main(string[] args) {
        if (args.Length != 2) {
            return Fail("usage: Smoke <fixture-path> <expected-substring>");
        }
        var fixturePath = args[0];
        var expectedSubstring = args[1];

        var inputJson = $"{{\"kind\":\"uri\",\"uri\":{System.Text.Json.JsonSerializer.Serialize(fixturePath)}}}";
        var input = ExtractInput.FromJson(inputJson);
        var config = ExtractionConfig.FromJson("{}");

        ExtractionResult result;
        try {
            result = await XbergConverter.ExtractAsync(input, config);
        } catch (Exception e) {
            return Fail($"ExtractAsync() threw for fixture {fixturePath}: {e}");
        }

        if (result.Results.Count == 0) {
            return Fail($"ExtractAsync() returned zero results for fixture {fixturePath}");
        }

        var content = result.Results[0].Content ?? string.Empty;
        if (!content.Contains(expectedSubstring, StringComparison.Ordinal)) {
            return Fail(
                $"expected substring '{expectedSubstring}' not found in extracted content for fixture " +
                $"{fixturePath}; got: {content}");
        }

        Console.WriteLine($"SMOKE TEST PASSED: found '{expectedSubstring}' in extracted content");
        return 0;
    }
}
