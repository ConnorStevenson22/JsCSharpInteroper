using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>R29: JSDoc as XML docs.</summary>
public sealed class XmlDocTests
{
    [Fact] // R29
    public void TextIsEscapedAndLinksAndCodeSpansAreCode()
    {
        Assert.Equal(["Tom &amp; Jerry: a &lt; b &gt; c"], XmlDoc.Lines("Tom & Jerry: a < b > c"));
        Assert.Equal(["Moves the <c>Marker</c> to <c>[x, y]</c>."], XmlDoc.Lines("Moves the {@link Marker} to `[x, y]`."));
        Assert.Equal(["See the marker &amp; its pin, or <c>Pin&lt;T&gt;</c>."], XmlDoc.Lines("See {@link Marker the marker & its pin}, or {@linkcode Pin<T>}."));
        Assert.Equal(["<c>Marker</c>, piped, <c>Marker</c>"], XmlDoc.Lines("{@link Marker}, {@link Marker | piped}, {@link Marker|}"));
        Assert.Equal(["Nested: {y}."], XmlDoc.Lines("Nested: {@link X {y}}.")); // the text ends at the first brace; the second is text
    }

    [Fact] // R29
    public void AUrlLinkIsAnHref()
    {
        Assert.Equal(["<see href=\"https://example.com/a?b=1&amp;c=&quot;\">the site</see>, <see href=\"https://example.com\">https://example.com</see>"],
            XmlDoc.Lines("{@link https://example.com/a?b=1&c=\" the site}, {@link https://example.com}"));
    }

    [Fact] // R29: XML 1.0 has no control characters but tab and line breaks
    public void ControlCharactersAreDropped() => Assert.Equal(["a\tb c"], XmlDoc.Lines("a\tb\u0001 \u001bc"));

    [Fact] // R29
    public void EachLineIsALine()
    {
        Assert.Equal(["First line.", "", "Second, after a blank one."], XmlDoc.Lines("First line.\r\n\nSecond, after a blank one.  \n"));
        Assert.Empty(XmlDoc.Lines(" \n "));
    }

    [Fact] // R29: @default, @since, @deprecated and @see are remarks; a one-line element stays on one line
    public void TagsAreRemarks()
    {
        var w = new CodeWriter();
        XmlDoc.Write(w, new Doc(Summary: "Two\nlines.", Default: "0", Since: "0.2", Deprecated: "", See: ["Marker"]));
        XmlDoc.Write(w, new Doc(Deprecated: "Use `zoom`."));
        Assert.Equal("""
            /// <summary>
            /// Two
            /// lines.
            /// </summary>
            /// <remarks>
            /// Default: 0
            /// Since: 0.2
            /// Deprecated.
            /// See: Marker
            /// </remarks>
            /// <remarks>Deprecated: Use <c>zoom</c>.</remarks>

            """.ReplaceLineEndings("\n"), w.ToString());
    }

    [Fact] // R29: no doc, no comment
    public void NoDocIsNothing()
    {
        var w = new CodeWriter();
        XmlDoc.Write(w, null);
        XmlDoc.Write(w, new Doc(Summary: ""));
        Assert.Equal("", w.ToString());
    }
}
