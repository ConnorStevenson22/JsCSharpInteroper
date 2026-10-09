using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>The shape of every live member (design §1). Data types are the same in both.</summary>
public enum Api
{
    Sync,
    Async,
}

public sealed record GeneratorOptions(Api Api, string Namespace);

/// <param name="Path">Relative to the output folder: <c>Basemap.cs</c>, <c>demo-maps.meta.js</c>.</param>
public sealed record GeneratedFile(string Path, string Text);

public sealed record GeneratorResult(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<GeneratedFile> Files);

/// <summary>Generates the C# bindings for an IR and a profile (design §4). It writes nothing to disk.</summary>
public static class Generator
{
    /// <exception cref="NotSupportedException">A selected type uses something the generator can't map yet.</exception>
    public static GeneratorResult Generate(IrDocument ir, Profile profile, GeneratorOptions options)
    {
        var selection = Selection.Select(ir, profile);
        var diagnostics = selection.Diagnostics.ToList();
        var types = new TypeMap(ir, profile, selection, diagnostics);
        var ns = options.Namespace;
        var files = new List<GeneratedFile>();
        void Add(string type, string text)
        {
            if (files.Any(f => f.Path == $"{type}.cs"))
            {
                throw new NotSupportedException($"{type}: two generated types have this name (R27).");
            }
            files.Add(new($"{type}.cs", text));
        }

        foreach (var selected in selection.Declarations)
        {
            switch (selected.Declaration)
            {
                case InterfaceDeclaration or AliasDeclaration { Type: ObjectType }:
                    Add(Naming.Type(selected.Declaration.Name), RecordEmitter.Data(types, ns, selected.Declaration));
                    break;
                case AliasDeclaration a when types.Union(Naming.Type(a.Name), a.Type) is { } type:
                    Add(Naming.Type(a.Name), ValueTypeEmitter.Emit(types, ns, type));
                    break;
                case EnumDeclaration e:
                    Add(Naming.Type(e.Name), ValueTypeEmitter.Emit(types, ns, types.Enum(e)));
                    break;
                case ClassDeclaration c:
                    Add(Naming.Type(c.Name), LiveEmitter.Class(types, profile, options, diagnostics, selected));
                    Add(TypeMap.SnapshotName(c), RecordEmitter.Snapshot(types, ns, c));
                    break;
            }
        }
        var modules = selection.Declarations.Where(s => s.Declaration is FunctionDeclaration).GroupBy(s => s.Module).ToList();
        foreach (var module in modules)
        {
            var name = LiveEmitter.ModuleClass(module.Key);
            Add(name, LiveEmitter.Module(types, profile, options, diagnostics, name, module.Key, module.Select(s => (FunctionDeclaration)s.Declaration)));
        }
        var classes = MetadataEmitter.ClassIds(types, selection);
        Add(types.Registry, MetadataEmitter.Registry(types, options, classes, modules.Select(m => m.Key)));
        files.Add(new(MetadataEmitter.FileName(ir), MetadataEmitter.Module(ir, profile, types, classes)));
        foreach (var type in types.Owned)
        {
            Add(type.Name, ValueTypeEmitter.Emit(types, ns, type));
        }
        return new(diagnostics, files);
    }
}
