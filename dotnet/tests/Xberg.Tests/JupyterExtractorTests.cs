using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Which representation of a cell output reaches the document.
/// </summary>
public class JupyterOutputRepresentationTests
{
    private static InternalDocument Extract(string notebook, OutputFormat format) =>
        new JupyterExtractor().Extract(
            Encoding.UTF8.GetBytes(notebook), "application/x-ipynb+json",
            new ExtractionConfig { OutputFormat = format });

    private static string Texts(InternalDocument doc) =>
        string.Join("\n", doc.Elements.Select(e => e.Text));

    private const string HtmlOutput = """
        {"cells":[{"cell_type":"code","source":["df"],"outputs":[
          {"output_type":"execute_result","data":{
            "text/plain":["<IPython.core.display.HTML object>"],
            "text/html":["<table><tr><td>1</td></tr></table>"]}}]}],
         "metadata":{},"nbformat":4,"nbformat_minor":5}
        """;

    [Fact]
    public void AnHtmlReprIsPreferredOverThePlaceholderTextBesideIt()
    {
        // The text/plain beside an HTML output is a placeholder for an object, not its content.
        string text = Texts(Extract(HtmlOutput, OutputFormat.Markdown));
        Assert.Contains("<table><tr><td>1</td></tr></table>", text);
        Assert.DoesNotContain("IPython.core.display.HTML object", text);
    }

    [Fact]
    public void PlainOutputTakesTheTextReprInstead()
    {
        // Rendering markup into a plain document would put HTML tags in it.
        string text = Texts(Extract(HtmlOutput, OutputFormat.Plain));
        Assert.Contains("IPython.core.display.HTML object", text);
        Assert.DoesNotContain("<table>", text);
    }

    [Fact]
    public void ACodeCellWithNoSourceContributesOnlyItsOutput()
    {
        // An empty code element leaves a blank block between the cell before it and the output.
        var doc = Extract("""
            {"cells":[{"cell_type":"code","source":[],"outputs":[
              {"output_type":"stream","text":["7.29.0\n"]}]}],
             "metadata":{},"nbformat":4,"nbformat_minor":5}
            """, OutputFormat.Plain);

        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.Code);
        Assert.Equal("7.29.0", Texts(doc));
    }
}
