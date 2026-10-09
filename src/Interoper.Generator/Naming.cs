using System.Text;

namespace Interoper.Generator;

/// <summary>C# names for JS names (mapping §6).</summary>
public static class Naming
{
    private static readonly HashSet<string> ObjectMembers =
        ["Equals", "Finalize", "GetHashCode", "GetType", "MemberwiseClone", "ReferenceEquals", "ToString"];

    /// <summary>
    /// Splits on non-alphanumerics and upper-cases the first letter of each part (<c>"simple-marker"</c> →
    /// <c>SimpleMarker</c>, <c>"displayLabel"</c> → <c>DisplayLabel</c>); a leading digit gets an underscore (<c>_3d</c>).
    /// </summary>
    public static string Pascal(string name)
    {
        var result = new StringBuilder(name.Length + 1);
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }
            result.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        if (result.Length > 0 && char.IsDigit(result[0]))
        {
            result.Insert(0, '_');
        }
        return result.ToString();
    }

    /// <summary>R27: a type keeps its TypeScript name in PascalCase.</summary>
    public static string Type(string name) => Pascal(name);

    /// <summary>
    /// R27: an anonymous type is named after its owner (<c>Feature.shape</c> → <c>FeatureShape</c>), and the wider
    /// type of a setter after it, with <c>Input</c> (<c>FeatureShapeInput</c>).
    /// </summary>
    public static string OwnedType(string ownerType, string member, bool input = false) =>
        ownerType + Pascal(member) + (input ? "Input" : "");

    /// <summary>
    /// R25: enum member names for literal values, in order. The empty string is <c>Empty</c>; a name taken by an
    /// earlier value gets a numeric suffix, reported as IOP2006.
    /// </summary>
    public static string[] EnumMembers(IEnumerable<string> values, string subject, ICollection<Diagnostic> diagnostics)
    {
        var taken = new HashSet<string>();
        return [.. values.Select(value =>
        {
            var name = Pascal(value) is { Length: > 0 } pascal ? pascal : "Empty";
            if (taken.Add(name))
            {
                return name;
            }
            var unique = Unique(taken, name);
            diagnostics.Add(new(Diagnostic.Renamed, subject, $"\"{value}\" is {unique}: {name} is taken (R25)"));
            return unique;
        })];
    }

    /// <summary>
    /// R26: claims <paramref name="name"/> (already PascalCased, with any <c>Async</c> suffix) for a JS member of a type.
    /// <paramref name="taken"/> holds the names claimed so far: the type's own name and the members it generates
    /// (<c>GetZoomAsync</c> for <c>zoom</c>, <c>Snapshot</c>, …). A name that collides with one of them or with an
    /// <c>object</c> member becomes <c>JsName</c>, reported as IOP2006.
    /// </summary>
    public static string Member(HashSet<string> taken, string name, string subject, ICollection<Diagnostic> diagnostics)
    {
        if (!ObjectMembers.Contains(name) && taken.Add(name))
        {
            return name;
        }
        var renamed = Unique(taken, "Js" + name);
        diagnostics.Add(new(Diagnostic.Renamed, subject, $"{renamed}: {name} is taken (R26)"));
        return renamed;
    }

    /// <summary><paramref name="name"/> if it's free, otherwise the first free <c>name2</c>, <c>name3</c>, …; claimed.</summary>
    private static string Unique(HashSet<string> taken, string name)
    {
        var unique = name;
        for (var n = 2; !taken.Add(unique); n++)
        {
            unique = name + n;
        }
        return unique;
    }
}
