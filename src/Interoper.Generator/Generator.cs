using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>The shape of every live member (design §1). Data types are the same in both.</summary>
public enum Api
{
    Sync,
    Async,
}

/// <param name="Namespace">The root namespace; a module's folders add to it (R27).</param>
public sealed record GeneratorOptions(Api Api, string Namespace);

/// <param name="Path">Relative to the output folder, with <c>/</c>, mirroring the namespace (R27): <c>Basemap.cs</c>,
/// <c>Layers/TileLayer.cs</c>, <c>demo-maps.meta.js</c>.</param>
public sealed record GeneratedFile(string Path, string Text);

public sealed record GeneratorResult(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<GeneratedFile> Files);

/// <summary>Generates the C# bindings for an IR and a profile (design §4). It writes nothing to disk.</summary>
public static class Generator
{
    /// <summary>
    /// What it can't map it skips, with a diagnostic (mapping §8); it throws only for a malformed IR or profile
    /// (<see cref="InvalidDataException"/>).
    /// </summary>
    public static GeneratorResult Generate(IrDocument ir, Profile profile, GeneratorOptions options)
    {
        var selection = Selection.Select(ir, profile);
        var diagnostics = selection.Diagnostics.ToList();
        var types = new TypeMap(ir, profile, selection.Declarations, diagnostics, options.Namespace);
        var root = options.Namespace;
        var files = new List<GeneratedFile>();
        // R27: the registry keeps its name; a later type whose path is taken, or whose name is a namespace's, is skipped,
        // reported as IOP2006.
        var paths = new HashSet<string> { $"{types.Registry}.cs" };
        var namespaces = selection.Declarations.Select(s => types.Namespace(s.Module)).SelectMany(Parents).ToHashSet();
        void Add(TypeName type, string text)
        {
            var path = Naming.FilePath(root, type.Namespace, type.Name);
            if (namespaces.Contains($"{type.Namespace}.{type.Name}"))
            {
                diagnostics.Add(new(Diagnostic.Renamed, type.Name, $"skipped: `{type.Namespace}.{type.Name}` is a namespace (R27)"));
            }
            else if (!paths.Add(path))
            {
                diagnostics.Add(new(Diagnostic.Renamed, type.Name, $"skipped: `{path}` is taken (R27)"));
            }
            else
            {
                files.Add(new(path, text));
            }
        }

        foreach (var selected in selection.Declarations)
        {
            switch (selected.Declaration)
            {
                case InterfaceDeclaration or AliasDeclaration { Type: ObjectType }:
                    Add(types.Name(selected.Declaration), RecordEmitter.Data(types, selected.Declaration));
                    break;
                case AliasDeclaration a when types.Union(types.Name(a), a.Type) is { } type:
                    Add(type.Type, ValueTypeEmitter.Emit(types, type));
                    break;
                case EnumDeclaration e:
                    Add(types.Name(e), ValueTypeEmitter.Emit(types, types.Enum(e)));
                    break;
                case ClassDeclaration c:
                    Add(types.Name(c), LiveEmitter.Class(types, profile, options, diagnostics, selected));
                    Add(types.Snapshot(c), RecordEmitter.Snapshot(types, c));
                    break;
            }
        }
        var modules = selection.Declarations.Where(s => s.Declaration is FunctionDeclaration).GroupBy(s => s.Module).ToList();
        foreach (var module in modules)
        {
            // R27: nothing refers to a module's static class, so it takes a free name instead.
            var ns = types.Namespace(module.Key);
            var (wanted, name) = (LiveEmitter.ModuleClass(module.Key), LiveEmitter.ModuleClass(module.Key));
            for (var n = 2; paths.Contains(Naming.FilePath(root, ns, name)); n++)
            {
                name = $"{wanted}{n}";
            }
            if (name != wanted)
            {
                diagnostics.Add(new(Diagnostic.Renamed, module.Key, $"its functions are {name}: {wanted} is taken (R27)"));
            }
            Add(new(ns, name), LiveEmitter.Module(types, profile, options, diagnostics, name, module.Key, module.Select(s => (FunctionDeclaration)s.Declaration)));
        }
        var classes = MetadataEmitter.ClassIds(types, selection);
        files.Add(new($"{types.Registry}.cs", MetadataEmitter.Registry(types, options, classes, modules.Select(m => m.Key))));
        files.Add(new(MetadataEmitter.FileName(ir), MetadataEmitter.Module(ir, profile, types, classes)));
        foreach (var type in types.Owned)
        {
            Add(type.Type, ValueTypeEmitter.Emit(types, type));
        }
        return new(diagnostics, files);

        // A module's namespace and the ones it's in, below the root: Root.A.B gives Root.A.B and Root.A.
        IEnumerable<string> Parents(string ns)
        {
            for (; ns.Length > root.Length; ns = ns[..ns.LastIndexOf('.')])
            {
                yield return ns;
            }
        }
    }
}
