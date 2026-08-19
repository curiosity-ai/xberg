using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The WebVTT extractor, ported from Rust <c>extractors/vtt.rs</c>.
/// </summary>
public class WebVttExtractorTests
{
    private static InternalDocument Parse(string source) =>
        new WebVttExtractor().Extract(Encoding.UTF8.GetBytes(source), "text/vtt", new ExtractionConfig());

    private static List<InternalElement> Cues(InternalDocument doc) =>
        doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).ToList();

    [Fact]
    public void OnlyThePayloadIsBodyTextAndTimingsBecomeAttributes()
    {
        // A subtitle's timing labels that cue; it says nothing about the document, so it must
        // not land in the body text the way it did when .vtt passed through as plain text.
        var doc = Parse("""
            WEBVTT

            00:00:00.000 --> 00:00:05.000
            Welcome to the demo.
            """);

        var cue = Assert.Single(Cues(doc));
        Assert.Equal("Welcome to the demo.", cue.Text);
        Assert.Equal("00:00:00.000", cue.Attributes!["start"]);
        Assert.Equal("00:00:05.000", cue.Attributes["end"]);
    }

    [Fact]
    public void TheSignatureLinesTrailingTextNamesTheTrack()
    {
        var doc = Parse("WEBVTT - Episode one\n\n00:00.000 --> 00:01.000\nHi.\n");
        Assert.Equal("Episode one", doc.Metadata.Title);
    }

    [Fact]
    public void NoteStyleAndRegionBlocksAreNeverContent()
    {
        var doc = Parse("""
            WEBVTT

            NOTE This is a comment.

            STYLE
            ::cue { color: yellow }

            REGION
            id:fred width:40%

            00:00:01.000 --> 00:00:02.000
            Only this is content.
            """);

        var cue = Assert.Single(Cues(doc));
        Assert.Equal("Only this is content.", cue.Text);
    }

    [Fact]
    public void ACueIdentifierIsCarriedAsAnAttributeNotAsText()
    {
        var doc = Parse("WEBVTT\n\nintro\n00:00:01.000 --> 00:00:02.000\nHello.\n");
        var cue = Assert.Single(Cues(doc));
        Assert.Equal("Hello.", cue.Text);
        Assert.Equal("intro", cue.Attributes!["cue_id"]);
    }

    [Fact]
    public void AVoiceSpanNamesItsSpeakerAndPrefixesTheLine()
    {
        var doc = Parse("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v.loud Roger>Watch out!</v>\n");
        var cue = Assert.Single(Cues(doc));
        Assert.Equal("Roger", cue.Attributes!["speaker"]);
        Assert.Equal("Roger: Watch out!", cue.Text);
    }

    [Fact]
    public void CueMarkupAndEscapesAreResolved()
    {
        var doc = Parse("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<i>a</i> &lt;b&gt; &amp; <00:00:01.500>c\n");
        Assert.Equal("a <b> & c", Assert.Single(Cues(doc)).Text);
    }

    [Fact]
    public void CueSettingsAfterTheEndTimestampAreNotPartOfIt()
    {
        var doc = Parse("WEBVTT\n\n00:00:01.000 --> 00:00:02.000 line:0 position:20%\nText.\n");
        Assert.Equal("00:00:02.000", Assert.Single(Cues(doc)).Attributes!["end"]);
    }

    [Fact]
    public void ShortTimestampsAreMinutesAndSeconds()
    {
        var doc = Parse("WEBVTT\n\n01:30.500 --> 02:00.000\nText.\n");
        var cue = Assert.Single(Cues(doc));
        Assert.Equal("00:01:30.500", cue.Attributes!["start"]);
        Assert.Equal("00:02:00.000", cue.Attributes["end"]);
    }

    [Fact]
    public void DurationIsTheLatestCueEnd()
    {
        var doc = Parse("""
            WEBVTT

            00:00:00.000 --> 00:00:05.000
            One.

            00:00:05.000 --> 00:00:12.250
            Two.
            """);
        Assert.Equal("00:00:12.250", doc.Metadata.Additional["duration"].GetString());
        Assert.Equal(2, doc.Metadata.Additional["cue_count"].GetInt32());
    }

    [Fact]
    public void UntimedTextIsKeptOnlyWhenThereAreNoCuesAtAll()
    {
        // Beside real cues an untimed block is noise. In a track with none — a plain file
        // mislabelled .vtt — it is the whole document, and dropping it would extract nothing.
        var alone = Parse("WEBVTT\n\nJust some prose.\n");
        Assert.Equal("Just some prose.", Assert.Single(Cues(alone)).Text);
        Assert.Equal(0, alone.Metadata.Additional["cue_count"].GetInt32());

        var beside = Parse("WEBVTT\n\nStray line\n\n00:00:01.000 --> 00:00:02.000\nReal cue.\n");
        Assert.Equal("Real cue.", Assert.Single(Cues(beside)).Text);
    }

    [Fact]
    public void AMissingSignatureIsReportedButTheCuesStillExtract()
    {
        var doc = Parse("00:00:01.000 --> 00:00:02.000\nStill content.\n");
        Assert.Equal("Still content.", Assert.Single(Cues(doc)).Text);
        Assert.Contains(doc.ProcessingWarnings, w => w.Message.Contains("missing WEBVTT signature"));
    }

    [Fact]
    public void AnUnparsableTimingLineCostsOnlyItsOwnCue()
    {
        var doc = Parse("""
            WEBVTT

            not:a:time --> nonsense
            Lost.

            00:00:01.000 --> 00:00:02.000
            Kept.
            """);
        Assert.Equal("Kept.", Assert.Single(Cues(doc)).Text);
        Assert.Contains(doc.ProcessingWarnings, w => w.Message.Contains("unparsable timing line"));
    }
}
