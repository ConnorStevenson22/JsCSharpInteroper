using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// The registry <c>{Package}.cs</c> (class ids, <c>JsClassRegistry</c>, sync mode's preloads, the import map) and the metadata module
/// <c>{package}.meta.js</c> (design §2, §3.4), which is the same in both modes.
/// </summary>
internal static class MetadataEmitter
{
    /// <summary>
    /// The live classes by class id, from 1 (0 is unknown): value-like classes first, then entities, each in IR order.
    /// That order gives the fixture the ids the transport contract hard-codes (design §2).
    /// </summary>
    public static IReadOnlyList<Selected> ClassIds(TypeMap types, Selection selection) =>
        [.. selection.Declarations.Where(s => s.Declaration is ClassDeclaration).OrderBy(s => !types.IsValueLike((ClassDeclaration)s.Declaration))];

    /// <summary>
    /// The class id constants and the registry of live types (R19 step 1), an abstract entity class's too; a closed one
    /// has none (it's read by value, R16), and nor has a generic one, whose readers make it (mapping §2). Sync mode preloads the modules of its classes and module functions
    /// and custom elements (design §3.5, R28): the modules they're imported from, which for an internal module's
    /// declarations are the modules that re-export them; one that can't be imported has none. Then the import map's
    /// entries, the same in both modes.
    /// </summary>
    /// <remarks>
    /// §5: a variant's class id follows the classes', with no constant: the registry makes it a private subclass of its
    /// base that implements its mixins' interfaces.
    /// </remarks>
    public static string Registry(IrDocument ir, TypeMap types, GeneratorOptions options, IReadOnlyList<Selected> classes, IReadOnlyList<SelectedVariant> variants,
        IEnumerable<Selected> functions, IEnumerable<SelectedElement> elements)
    {
        var w = CodeWriter.CSharp(options.Namespace);
        using (w.Block($"public static partial class {types.Registry}"))
        {
            foreach (var (s, id) in classes.Select((s, i) => (s, i + 1)))
            {
                w.Line($"public const int {types.ClassIdName((ClassDeclaration)s.Declaration)} = {id};");
            }
            if (classes.Count > 0)
            {
                w.Line();
            }
            var calls = classes.Select(s => (ClassDeclaration)s.Declaration).Where(c => !types.IsClosed(c) && c.TypeParameters.Count == 0)
                .Select(c => $".Add({types.ClassIdName(c)}, static (realm, handle) => new {types.Name(c).In(options.Namespace)}(realm, handle))").ToList();
            var taken = new HashSet<string>(classes.Select(s => types.ClassIdName((ClassDeclaration)s.Declaration))) { types.Registry, "Classes", "ImportMap" };
            var nested = variants.Select(v => (Variant: v, Name: Naming.Unique(taken, Naming.Type(v.Class.Name)))).ToList();
            calls.AddRange(nested.Select((v, i) => $".Add({classes.Count + i + 1}, static (realm, handle) => new {v.Name}(realm, handle))"));
            var modules = classes.Select(s => s.Declaration).Concat(functions.Select(s => s.Declaration)).Concat(elements.Select(e => e.Class))
                .Select(types.Import).OfType<(string Module, string Export)>().Select(i => i.Module).Distinct().ToList();
            if (options.Api == Api.Sync && modules.Count > 0)
            {
                calls.Add($".Preload({string.Join(", ", modules.Select(Syntax.Literal))})");
            }
            w.Line($"public static global::Interoper.JsClassRegistry Classes {{ get; }} = new global::Interoper.JsClassRegistry(){(calls.Count == 0 ? ";" : "")}");
            using (w.Indent())
            {
                foreach (var call in calls)
                {
                    w.Line(call == calls[^1] ? call + ";" : call);
                }
            }
            w.Line();
            w.Line("/// <summary>The import map's entries for the packages' modules; <paramref name=\"root\"/> holds the packages' folders, as <c>node_modules</c> does (<c>\"/node_modules/\"</c>), and starts with <c>/</c>, <c>./</c> or <c>../</c>, or is a URL.</summary>");
            using (w.Block("public static global::System.Collections.Generic.IReadOnlyDictionary<string, string> ImportMap(string root)"))
            {
                w.Line("root = global::Interoper.JsImportMap.Root(root);");
                using (w.Block("return new global::System.Collections.Generic.Dictionary<string, string>", "};"))
                {
                    foreach (var (specifier, path) in ImportMap(ir))
                    {
                        w.Line($"[{Syntax.Literal(specifier)}] = root + {Syntax.Literal(path)},");
                    }
                }
            }
            foreach (var (v, name) in nested)
            {
                var interfaces = string.Concat(v.Mixins.Select(m => ", " + types.MixinName(m).In(options.Namespace)));
                w.Line();
                w.Line($"private sealed class {name}(global::Interoper.JsRealm realm, int handle) : {types.Name(v.Base).In(options.Namespace)}(realm, handle){interfaces};");
            }
        }
        return w.ToString();
    }

    /// <summary>
    /// Design §3.5: a prefix entry per package, its folder (<c>"@acme/maps/"</c>), and an exact one per module whose
    /// file an export maps elsewhere (<c>"@acme/maps" → "@acme/maps/dist/index.js"</c>), in ordinal order.
    /// </summary>
    private static IEnumerable<(string Specifier, string Path)> ImportMap(IrDocument ir)
    {
        var mapped = ir.Modules.Select(m => (m.Specifier, m.File))
            .Concat(ir.Modules.SelectMany(m => m.Declarations).Select(d => d.ImportFrom).OfType<ImportFrom>().Select(i => (Specifier: i.Module, i.File)))
            .ToList();
        var packages = mapped.Select(m => PackageName(m.Specifier)).Select(p => (Specifier: p + "/", Path: p + "/"));
        var files = mapped.Where(m => m.File is not null).Select(m => (m.Specifier, Path: $"{PackageName(m.Specifier)}/{m.File}"));
        return packages.Concat(files).Distinct().OrderBy(e => e.Specifier, StringComparer.Ordinal);
    }

    /// <summary>A specifier's package, as npm names them: its first segment, or its first two when it's scoped.</summary>
    private static string PackageName(string specifier) =>
        string.Join('/', specifier.Split('/').Take(specifier.StartsWith('@') ? 2 : 1));

    /// <summary>
    /// Per class id: the module and export that construct it (an omitted export is <c>default</c>), its runtime class name
    /// (with the profile's prefix when it's the first package's), or a custom element's tag instead (R28), since JS finds
    /// an element by its tag, whether it's value-like (R16), and its snapshot members (R18), a value-like one's with its
    /// constants, when it has any. Then the components' custom events, each with its detail's default projection (R24, R28).
    /// </summary>
    /// <remarks>§5: a variant's entry is its runtime class name and the class id whose entry it shares (<c>of</c>).</remarks>
    public static string Module(IrDocument ir, Profile profile, TypeMap types, IReadOnlyList<Selected> classes, IReadOnlyList<SelectedVariant> variants,
        IReadOnlyList<SelectedElement> elements)
    {
        // The profile is the first package's, and so is its prefix: another package's classes have none.
        string Named(string module, string name) =>
            (types.PackageOf(module) == ir.Package.Name ? profile.RuntimeClassPrefix : "") + RuntimeName(types.PackageOf(module), module, name);
        var lines = new List<string> { Bindings.Header, "export default {" };
        if (profile.RuntimeClassProperty is { } property)
        {
            lines.Add($"  classNameProperty: {Syntax.Literal(property)},");
        }
        lines.Add("  classes: {");
        foreach (var (s, id) in classes.Select((s, i) => (s, i + 1)))
        {
            var c = (ClassDeclaration)s.Declaration;
            var members = string.Join(", ", types.WireMembers(c).Select(m => Syntax.Literal(m.Name)));
            var tag = elements.FirstOrDefault(e => e.Class.Name == c.Name && e.Module == s.Module)?.Element.Tag;
            List<string> fields = [];
            // Design §3.5: a class that can't be imported has neither, so it isn't constructed or imported for $c.
            if (types.Import(c) is { } i)
            {
                fields.Add($"module: {Syntax.Literal(i.Module)}");
                if (i.Export != "default")
                {
                    fields.Add($"export: {Syntax.Literal(i.Export)}");
                }
            }
            fields.Add(tag is null ? $"name: {Syntax.Literal(Named(s.Module, c.Name))}" : $"tag: {Syntax.Literal(tag)}");
            if (types.IsValueLike(c))
            {
                fields.Add("value: true");
            }
            if (members.Length > 0)
            {
                fields.Add($"members: [{members}]");
            }
            lines.Add($"    {id}: {{ {string.Join(", ", fields)} }},");
        }
        foreach (var (v, id) in variants.Select((v, i) => (v, classes.Count + i + 1)))
        {
            var of = classes.Select((s, i) => (s, i + 1)).First(x => x.s.Declaration == v.Base).Item2;
            lines.Add($"    {id}: {{ name: {Syntax.Literal(Named(v.Module, v.Class.Name))}, of: {of} }},");
        }
        lines.Add("  },");
        // One registration per event name, so elements that share a name share its fields: all of theirs, or the whole
        // detail when one of them has no projection. An element without a detail sends none, whatever is projected.
        var events = elements.SelectMany(e => e.Element.Events).GroupBy(e => e.Name).ToList();
        if (events.Count > 0)
        {
            lines.Add("  customEvents: [");
            foreach (var e in events)
            {
                var projections = e.Where(x => x.Detail is not null).Select(x => types.Projection(x.Detail!)).ToList();
                var fields = projections.Count > 0 && projections.All(p => p is not null)
                    ? $", fields: [{string.Join(", ", projections.SelectMany(p => p!).Distinct().Select(Syntax.Literal))}]"
                    : "";
                lines.Add($"    {{ name: {Syntax.Literal(e.Key)}{fields} }},");
            }
            lines.Add("  ],");
        }
        lines.AddRange(["};", ""]);
        return string.Join("\n", lines);
    }

    /// <summary>The metadata module's file: the package's name without its scope (<c>demo-maps.meta.js</c>).</summary>
    public static string FileName(IrDocument ir) => ir.Package.Name.Split('/')[^1] + ".meta.js";

    /// <summary>
    /// The runtime class name: the module's path in its package, dotted, plus the declaration's name unless the file is
    /// named after it (<c>geometry.Point</c>, <c>MapView</c>, <c>layers.TileLayer</c> for a default export); the name
    /// alone for the package's root export.
    /// </summary>
    private static string RuntimeName(string package, string specifier, string name)
    {
        var path = Naming.PathInPackage(package, specifier);
        path = (path.EndsWith(".js", StringComparison.Ordinal) ? path[..^3] : path).Replace('/', '.');
        return path.Length == 0 ? name : path.Split('.')[^1] == name ? path : $"{path}.{name}";
    }
}
