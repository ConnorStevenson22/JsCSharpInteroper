using System.Text;

namespace Interoper.Generator;

/// <summary>C# spellings that generated code needs: literals and local names.</summary>
internal static class Syntax
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    ];

    /// <summary>A C# string literal: <c>"3d-relief"</c>.</summary>
    public static string Literal(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return text.Append('"').ToString();
    }

    /// <summary>A UTF-8 string literal: <c>"minX"u8</c>.</summary>
    public static string Utf8(string value) => Literal(value) + "u8";

    /// <summary>
    /// A local for a PascalCased name (<c>MinX</c> → <c>minX</c>), clear of C# keywords and of the names generated code
    /// already uses in that method (<paramref name="taken"/>).
    /// </summary>
    public static string Local(string name, params string[] taken)
    {
        var local = char.ToLowerInvariant(name[0]) + name[1..];
        if (taken.Contains(local))
        {
            local += "Value";
        }
        return Keywords.Contains(local) ? "@" + local : local;
    }
}
