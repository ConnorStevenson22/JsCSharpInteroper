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
        string?[] remarks =
        [
            doc.Default is { } value ? "Default: " + value : null,
            doc.Since is { } since ? "Since: " + since : null,
            doc.Deprecated is { } deprecated ? deprecated.Length == 0 ? "Deprecated." : "Deprecated: " + deprecated : null,
            .. (doc.See ?? []).Select(see => "See: " + see),
        ];
        Element(w, "remarks", [.. remarks.SelectMany(Lines)]);
    }

    /// <summary>
    /// JSDoc text as XML doc lines, a line per line: XML-escaped, control characters but tab dropped, <c>{@link X}</c>
    /// as <c>&lt;c&gt;X&lt;/c&gt;</c>, <c>{@link X text}</c> as its text, a URL's as <c>&lt;see href&gt;</c>, and a
    /// <c>`code`</c> span as <c>&lt;c&gt;code&lt;/c&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> Lines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        return [.. text.ReplaceLineEndings("\n").Trim().Split('\n').Select(line => Inline().Replace(line.TrimEnd(), Format))];
    }

    private static string Format(Match m)
    {
        if (m.Groups["code"].Success)
        {
            return $"<c>{Escape(m.Groups["code"].Value)}</c>";
        }
        if (!m.Groups["name"].Success)
        {
            return Escape(m.Value);
        }
        var (name, text) = (m.Groups["name"].Value, m.Groups["text"].Value);
        return name.Contains("://", StringComparison.Ordinal) ? $"<see href=\"{Escape(name).Replace("\"", "&quot;")}\">{Escape(text.Length > 0 ? text : name)}</see>"
            : text.Length > 0 ? Escape(text)
            : $"<c>{Escape(name)}</c>";
    }

    private static string Escape(string text) =>
        string.Concat(text.Where(c => c >= ' ' || c == '\t')).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

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

    /// <summary>A link (its name, then its text if any, after a space or <c>|</c>), a code span, or one character to escape.</summary>
    [GeneratedRegex(@"\{@link(?:code|plain)?\s+(?<name>[^\s}|]+)(?:[\s|]+(?<text>[^}]*?))?\s*\}|`(?<code>[^`]+)`|[&<>\x00-\x08\x0A-\x1F]")]
    private static partial Regex Inline();
}
