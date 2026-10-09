using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>The shape of every live member (design §1). Data types are the same in both.</summary>
public enum Api
{
    Sync,
    Async,
}

public sealed record GeneratorOptions(Api Api, string Namespace);

/// <param name="Path">Relative to the output folder: <c>Basemap.cs</c>.</param>
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
        void Add(string type, string text) => files.Add(new($"{type}.cs", text));

        foreach (var declaration in selection.Declarations.Select(s => s.Declaration))
        {
            switch (declaration)
            {
                case InterfaceDeclaration or AliasDeclaration { Type: ObjectType }:
                    Add(Naming.Type(declaration.Name), RecordEmitter.Data(types, ns, declaration));
                    break;
                case AliasDeclaration a when types.Union(Naming.Type(a.Name), a.Type) is { } type:
                    Add(Naming.Type(a.Name), ValueTypeEmitter.Emit(types, ns, type));
                    break;
                case EnumDeclaration e:
                    Add(Naming.Type(e.Name), ValueTypeEmitter.Emit(types, ns, types.Enum(e)));
                    break;
                case ClassDeclaration c:
                    types.MapProperties(c);
                    Add(TypeMap.SnapshotName(c), RecordEmitter.Snapshot(types, ns, c));
                    break;
            }
        }
        foreach (var type in types.Owned)
        {
            Add(type.Name, ValueTypeEmitter.Emit(types, ns, type));
        }
        return new(diagnostics, files);
    }
}
