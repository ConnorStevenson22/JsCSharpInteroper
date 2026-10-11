using System.Text.RegularExpressions;
using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// R29: a declaration's JSDoc (or manifest docs) as XML docs: <c>&lt;summary&gt;</c>, and <c>@default</c>,
/// <c>@since</c>, <c>@deprecated</c> and <c>@see</c> as lines of <c>&lt;remarks&gt;</c>. No doc, nothing.
/// </summary>
public static partial class XmlDoc
{
    /// <summary>Writes the doc comment of the member or type the writer writes next.</summary>
    public static void Write(CodeWriter w, Doc? doc)
    {
        if (doc is null)
        {
            return;
        }
        Element(w, "summary", Lines(doc.Summary));
        var remarks = new List<string>();
        if (doc.Default is { } value)
        {
            remarks.AddRange(Lines("Default: " + value));
        }
        if (doc.Since is { } since)
        {
            remarks.AddRange(Lines("Since: " + since));
        }
        if (doc.Deprecated is { } deprecated)
        {
            remarks.AddRange(Lines(deprecated.Length == 0 ? "Deprecated." : "Deprecated: " + deprecated));
        }
        foreach (var see in doc.See ?? [])
        {
            remarks.AddRange(Lines("See: " + see));
        }
        Element(w, "remarks", remarks);
    }

    /// <summary>
    /// JSDoc text as XML doc lines, a line per line: XML-escaped, <c>{@link X}</c> as <c>&lt;c&gt;X&lt;/c&gt;</c>,
    /// <c>{@link X text}</c> as its text, and a <c>`code`</c> span as <c>&lt;c&gt;code&lt;/c&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> Lines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        return [.. text.ReplaceLineEndings("\n").Trim().Split('\n').Select(line => Inline().Replace(line.TrimEnd(), Format))];
    }

    private static string Format(Match m) =>
        m.Groups["code"].Success ? $"<c>{Escape(m.Groups["code"].Value)}</c>"
        : m.Groups["text"].Success ? Escape(m.Groups["text"].Value)
        : m.Groups["name"].Success ? $"<c>{Escape(m.Groups["name"].Value)}</c>"
        : Escape(m.Value);

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void Element(CodeWriter w, string name, IReadOnlyList<string> lines)
    {
        if (lines is [var line])
        {
            w.Line($"/// <{name}>{line}</{name}>");
        }
        else if (lines.Count > 0)
        {
            w.Line($"/// <{name}>");
            foreach (var l in lines)
            {
                w.Line(l.Length == 0 ? "///" : "/// " + l);
            }
            w.Line($"/// </{name}>");
        }
    }

    /// <summary>A link (its name, then its text if any), a code span, or one character to escape.</summary>
    [GeneratedRegex(@"\{@link(?:code|plain)?\s+(?<name>[^\s}|]+)(?:[\s|]+(?<text>[^}]+?))?\s*\}|`(?<code>[^`]+)`|[&<>]")]
    private static partial Regex Inline();
}
