using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.Code;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The source-code extractor and the language detection it rests on, ported from Rust
/// <c>extractors/code.rs</c> and <c>tree_sitter_language_pack</c>'s detection tables.
/// </summary>
public class CodeExtractorTests
{
    [Theory]
    [InlineData("py", "python")]
    [InlineData("PY", "python")]
    [InlineData("rs", "rust")]
    [InlineData("js", "javascript")]
    [InlineData("tsx", "tsx")]
    [InlineData("mmd", "mermaid")]
    [InlineData("puml", "plantuml")]
    [InlineData("xyz", null)]
    [InlineData("", null)]
    public void AnExtensionNamesItsLanguage(string ext, string? expected) =>
        Assert.Equal(expected, CodeLanguages.FromExtension(ext));

    /// <summary>
    /// A compound suffix is matched against the whole file name, because the generated table
    /// holds only single dot-free keys — so `foo.app.src` has nowhere else to resolve, and
    /// `page.rs.html` would otherwise come back as plain HTML.
    /// </summary>
    [Theory]
    [InlineData("myapp.app.src", "erlang")]
    [InlineData("FOO.APP.SRC", "erlang")]
    [InlineData("notes.src", null)]
    [InlineData("templates/page.rs.html", "rshtml")]
    [InlineData("page.html", "html")]
    [InlineData(".config/kitty/kitty.conf", "kitty")]
    [InlineData("service/.env", "dotenv")]
    [InlineData("Makefile", null)]
    [InlineData("", null)]
    public void APathResolvesCompoundSuffixesBeforeItsExtension(string path, string? expected) =>
        Assert.Equal(expected, CodeLanguages.FromPath(path));

    [Theory]
    [InlineData("#!/usr/bin/env python3\npass", "python")]
    [InlineData("#!/bin/bash\necho hi", "bash")]
    [InlineData("#!/usr/bin/env node", "javascript")]
    [InlineData("#!/usr/bin/env -S ruby3.2 -w", "ruby")]
    [InlineData("﻿#!/usr/bin/env python3\npass", "python")]
    [InlineData("no shebang here", null)]
    [InlineData("#!", null)]
    public void AShebangNamesItsInterpretersLanguage(string content, string? expected) =>
        Assert.Equal(expected, CodeLanguages.FromContent(content));

    /// <summary>
    /// The source is emitted verbatim in a single code element carrying the detected language,
    /// which is what upstream produces whenever tree-sitter returns no chunks — every source
    /// fixture in the corpus, under the default configuration.
    /// </summary>
    [Fact]
    public void SourceIsOneVerbatimCodeElementCarryingItsLanguage()
    {
        const string source = "def greet(name):\n    return f\"Hello, {name}!\"\n";
        var doc = new CodeExtractor().Extract(
            Encoding.UTF8.GetBytes(source),
            CodeExtractor.SourceCodeMimeType,
            new ExtractionConfig { SourceName = "greet.py" });

        var element = Assert.Single(doc.Elements);
        Assert.Equal(ElementKindTag.Code, element.Kind.Tag);
        Assert.Equal(source, element.Text);
        Assert.Equal("code", doc.Metadata.Format!.FormatType);
        Assert.Equal(CodeExtractor.SourceCodeMimeType, doc.MimeType);
    }

    /// <summary>
    /// Content decides before the name does: a shebang is what the file says about itself, and
    /// it outranks whatever the extension claims.
    /// </summary>
    [Fact]
    public void AShebangOutranksTheExtension()
    {
        var doc = new CodeExtractor().Extract(
            Encoding.UTF8.GetBytes("#!/usr/bin/env node\nconsole.log(1)\n"),
            CodeExtractor.SourceCodeMimeType,
            new ExtractionConfig { SourceName = "thing.py" });

        Assert.Equal("javascript", Assert.Single(doc.Elements).Attributes!["language"]);
    }

    [Fact]
    public void SourceWithNoDetectableLanguageIsRefused()
    {
        Assert.Throws<NotSupportedException>(() => new CodeExtractor().Extract(
            Encoding.UTF8.GetBytes("just some words"),
            CodeExtractor.SourceCodeMimeType,
            new ExtractionConfig()));
    }

    /// <summary>
    /// Source-code detection runs only after the format table has had its say, so an extension
    /// that is both a language tree-sitter knows and a format xberg handles stays a format.
    /// </summary>
    [Theory]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("data.json", "application/json")]
    [InlineData("conf.yaml", "application/x-yaml")]
    [InlineData("page.html", "text/html")]
    [InlineData("main.py", Mime.CodeMimeType)]
    [InlineData("app.js", Mime.CodeMimeType)]
    public void TheFormatTableOutranksLanguageDetection(string path, string expected) =>
        Assert.Equal(expected, Mime.DetectMimeType(path, checkExists: false));

    [Fact]
    public void LanguageDetectionCanBeTurnedOff()
    {
        // Upstream gates it behind a cargo feature; a build without it reads the same file as
        // plain text, and the two golden sets differ accordingly.
        Assert.Null(Mime.DetectMimeType("main.py", checkExists: false, sourceCode: false));
        Assert.Equal(Mime.CodeMimeType, Mime.DetectMimeType("main.py", checkExists: false, sourceCode: true));
    }
}
