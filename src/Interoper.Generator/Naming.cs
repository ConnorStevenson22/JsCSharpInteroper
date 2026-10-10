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
    /// R27: a module's namespace is the root namespace plus the module's folders in its package, each segment
    /// PascalCased: <c>@acme/maps/layers/TileLayer.js</c> is in <c>Acme.Maps.Layers</c>. A module of a package other
    /// than the IR's (<paramref name="otherPackage"/>) has that package's name without its scope first:
    /// <c>@acme/widgets/legend</c> is in <c>Acme.Maps.Widgets</c>.
    /// </summary>
    public static string Namespace(string root, string package, string specifier, bool otherPackage = false)
    {
        var folders = PathInPackage(package, specifier).Split('/')[..^1].Select(Pascal);
        return string.Join(".", (otherPackage ? folders.Prepend(Pascal(package.Split('/')[^1])) : folders).Prepend(root));
    }

    /// <summary>A module's path in its package: <c>layers/TileLayer.js</c>, or empty for the package's root export.</summary>
    public static string PathInPackage(string package, string specifier) =>
        specifier == package ? "" : specifier.StartsWith(package + "/", StringComparison.Ordinal) ? specifier[(package.Length + 1)..] : specifier;

    /// <summary>
    /// R27: a generated file's path mirrors its namespace below the root namespace: <c>Layers/TileLayer.cs</c> for
    /// <c>Acme.Maps.Layers.TileLayer</c>.
    /// </summary>
    public static string FilePath(string root, string ns, string type) =>
        ns == root ? $"{type}.cs" : $"{ns[(root.Length + 1)..].Replace('.', '/')}/{type}.cs";

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

/// <summary>A generated type's namespace and name (R27).</summary>
internal sealed record TypeName(string Namespace, string Name)
{
    /// <summary>Its name with its namespace, as any namespace names it: <c>global::Acme.Maps.Layers.TileLayer</c>.</summary>
    public string FullName => $"global::{Namespace}.{Name}";

    /// <summary>How code in namespace <paramref name="ns"/> names it: plainly in its own namespace, otherwise <see cref="FullName"/>.</summary>
    public string In(string ns) => ns == Namespace ? Name : FullName;

    /// <summary>R27: an anonymous type named after this one (<see cref="Naming.OwnedType"/>), in its namespace.</summary>
    public TypeName Owned(string member) => this with { Name = Naming.OwnedType(Name, member) };

    public override string ToString() => $"{Namespace}.{Name}";
}
