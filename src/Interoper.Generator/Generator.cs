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
        ir = Instantiation.Instantiate(ir); // §2: a generic union alias's uses are its unions
        var selection = Selection.Select(ir, profile);
        var diagnostics = selection.Diagnostics.ToList();
        var types = new TypeMap(ir, profile, selection.Declarations, diagnostics, options.Namespace);
        var root = options.Namespace;
        var files = new List<GeneratedFile>();
        // R27: the registry keeps its path. A later type whose path is taken, or matches a namespace, is skipped with
        // IOP2006, and generated code that uses it won't compile. Case is ignored, as on Windows and macOS.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{types.Registry}.cs" };
        var namespaces = types.Namespaces;
        var declared = new Dictionary<ClassDeclaration, IReadOnlySet<string>>(ReferenceEqualityComparer.Instance); // R26, per class
        void Add(TypeName type, string text)
        {
            var path = Naming.FilePath(root, type.Namespace, type.Name);
            if (namespaces.Contains(type.ToString()))
            {
                diagnostics.Add(new(Diagnostic.Renamed, type.ToString(), "skipped: a namespace has its name (R27)"));
            }
            else if (!paths.Add(path))
            {
                diagnostics.Add(new(Diagnostic.Renamed, type.ToString(), $"skipped: `{path}` is taken (R27)"));
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
                    Add(type.Type, ValueTypeEmitter.Emit(types, type, a));
                    break;
                case EnumDeclaration e:
                    Add(types.Name(e), ValueTypeEmitter.Emit(types, types.Enum(e), e));
                    break;
                case ClassDeclaration c:
                    Add(types.Name(c), LiveEmitter.Class(types, profile, options, diagnostics, declared, c));
                    Add(types.Snapshot(c), RecordEmitter.Snapshot(types, c));
                    break;
            }
        }
        var modules = selection.Declarations.Where(s => s.Declaration is FunctionDeclaration).GroupBy(s => s.Module).ToList();
        foreach (var module in modules)
        {
            // R27: nothing refers to a module's static class, so it takes a free name instead.
            var ns = types.Namespace(module.Key);
            var wanted = LiveEmitter.ModuleClass(module.Key);
            var name = wanted;
            for (var n = 2; paths.Contains(Naming.FilePath(root, ns, name)) || namespaces.Contains($"{ns}.{name}"); n++)
            {
                name = $"{wanted}{n}";
            }
            if (name != wanted)
            {
                diagnostics.Add(new(Diagnostic.Renamed, module.Key, $"its functions are {name}: {wanted} is taken (R27)"));
            }
            Add(new(ns, name), LiveEmitter.Module(types, profile, options, diagnostics, name, module.Key, module.Select(s => (FunctionDeclaration)s.Declaration)));
        }
        foreach (var element in selection.Elements)
        {
            // R28: a component takes a free name: its tag's, else with Component (common, not reported), else with a
            // number (IOP2006). A name taken in another case counts, since the files would clash.
            var ns = types.Namespace(element.Class);
            var wanted = Naming.Type(element.Element.Tag);
            // An inline record or anonymous union named already takes its name too, and the component's is reserved.
            bool Taken(string name) => paths.Contains(Naming.FilePath(root, ns, name)) || namespaces.Contains($"{ns}.{name}") || types.Taken(new(ns, name));
            var name = Taken(wanted) ? wanted + "Component" : wanted;
            for (var n = 2; Taken(name); n++)
            {
                name = $"{wanted}Component{n}";
            }
            if (name != wanted && name != wanted + "Component")
            {
                diagnostics.Add(new(Diagnostic.Renamed, $"<{element.Element.Tag}>", $"its component is {name}: {wanted} and {wanted}Component are taken (R28)"));
            }
            types.Reserve(new(ns, name));
            Add(new(ns, name), ComponentEmitter.Component(types, options, diagnostics, element, name));
        }
        var classes = MetadataEmitter.ClassIds(types, selection);
        files.Add(new($"{types.Registry}.cs", MetadataEmitter.Registry(ir, types, options, classes, modules.SelectMany(m => m), selection.Elements)));
        files.Add(new(MetadataEmitter.FileName(ir), MetadataEmitter.Module(ir, profile, types, classes, selection.Elements)));
        // The anonymous unions and inline records (R11, R27), unions first; emitting one can meet more of them.
        var (unions, records) = (0, 0);
        while (unions < types.Owned.Count || records < types.Records.Count)
        {
            if (unions < types.Owned.Count)
            {
                var type = types.Owned[unions++];
                Add(type.Type, ValueTypeEmitter.Emit(types, type));
                continue;
            }
            var record = types.Records[records++];
            Add(types.Name(record), RecordEmitter.Data(types, record));
        }
        // R28: Razor finds custom events' [EventHandler]s only on a class named EventHandlers. It's partial, so the
        // bindings of two packages in one namespace share it; a type of that name in the root namespace takes it.
        var events = selection.Elements.SelectMany(e => e.Element.Events).Select(e => e.Name).Distinct().ToList();
        var eventsPath = Naming.FilePath(root, root, $"{types.Registry}Events");
        if (events.Count > 0 && (paths.Contains(Naming.FilePath(root, root, "EventHandlers")) || namespaces.Contains($"{root}.EventHandlers") || !paths.Add(eventsPath)))
        {
            diagnostics.Add(new(Diagnostic.Renamed, $"{root}.EventHandlers", $"skipped: the custom events' handlers need `{root}.EventHandlers` and `{eventsPath}` (R28)"));
        }
        else if (events.Count > 0)
        {
            files.Add(new(eventsPath, ComponentEmitter.Events(root, events)));
        }
        types.ReportEnumRenames();
        return new(diagnostics, files);
    }
}
