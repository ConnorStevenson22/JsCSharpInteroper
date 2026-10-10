using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// Mapping §2: a use of a generic union alias (<c>MarkerList&lt;Marker&gt;</c>, where
/// <c>type MarkerList&lt;T&gt; = readonly T[] | Collection&lt;T&gt;</c>) is the union it stands for, with the type
/// arguments in place of the type parameters, as if it were written there: an anonymous union named after its owner
/// (R27). A C# union struct isn't generic, so the alias has no type of its own.
/// </summary>
internal static class Instantiation
{
    /// <summary>How deep an alias may expand into others, so a recursive one stops (it's left as it is, and skipped).</summary>
    private const int MaxDepth = 8;

    /// <summary>The IR with every use of a generic union alias replaced by its instantiated union.</summary>
    public static IrDocument Instantiate(IrDocument ir)
    {
        var aliases = ir.Modules.SelectMany(m => m.Declarations.OfType<AliasDeclaration>().Where(a => a.TypeParameters.Count > 0 && a.Type is UnionType)
            .Select(a => (Key: (m.Specifier, a.Name), Alias: a))).ToDictionary(x => x.Key, x => x.Alias);
        if (aliases.Count == 0)
        {
            return ir;
        }
        var rewriter = new Rewriter(aliases);
        return ir with
        {
            Modules = [.. ir.Modules.Select(m => m with { Declarations = [.. m.Declarations.Select(rewriter.Declaration)] })],
            Elements = [.. ir.Elements.Select(rewriter.Element)],
        };
    }

    private sealed class Rewriter(Dictionary<(string Module, string Name), AliasDeclaration> aliases)
    {
        public Declaration Declaration(Declaration d) => d switch
        {
            ClassDeclaration c => c with
            {
                Extends = c.Extends is { } e ? Type(e) : null,
                Implements = [.. c.Implements.Select(t => Type(t))],
                Constructors = [.. c.Constructors.Select(Signature)],
                Members = [.. c.Members.Select(Member)],
                Events = [.. c.Events.Select(e => e with { Type = Type(e.Type) })],
            },
            InterfaceDeclaration i => i with
            {
                Extends = [.. i.Extends.Select(t => Type(t))],
                Members = [.. i.Members.Select(Member)],
                IndexSignatures = [.. i.IndexSignatures.Select(x => x with { Type = Type(x.Type) })],
                CallSignatures = [.. i.CallSignatures.Select(Signature)],
                ConstructSignatures = [.. i.ConstructSignatures.Select(Signature)],
            },
            AliasDeclaration a => a with { Type = Type(a.Type) },
            FunctionDeclaration f => f with { Signatures = [.. f.Signatures.Select(Signature)] },
            VariableDeclaration v => v with { Type = Type(v.Type) },
            _ => d,
        };

        public Element Element(Element e) => e with
        {
            Attributes = [.. e.Attributes.Select(a => a with { Type = a.Type is { } t ? Type(t) : null })],
            Events = [.. e.Events.Select(x => x with { Detail = x.Detail is { } d ? Type(d) : null, Type = x.Type is { } t ? Type(t) : null })],
        };

        private Member Member(Member m) => m switch
        {
            PropertyMember p => p with { Type = Type(p.Type), WriteType = p.WriteType is { } w ? Type(w) : null },
            MethodMember method => method with { Signatures = [.. method.Signatures.Select(Signature)] },
            _ => m,
        };

        private Signature Signature(Signature s) => s with
        {
            Params = [.. s.Params.Select(p => p with { Type = Type(p.Type) })],
            Returns = s.Returns is { } r ? Type(r) : null,
        };

        private IrType Type(IrType type, int depth = 0) => type switch
        {
            RefType { Module: { } module } r when depth < MaxDepth && aliases.TryGetValue((module, r.Name), out var alias) && r.Args.Count == alias.TypeParameters.Count =>
                Type(Substitute(alias.Type, alias.TypeParameters.Select(t => t.Name).Zip(r.Args.Select(a => Type(a, depth))).ToDictionary()), depth + 1),
            RefType r => r with { Args = [.. r.Args.Select(a => Type(a, depth))] },
            ArrayType a => a with { Element = Type(a.Element, depth) },
            TupleType t => t with { Elements = [.. t.Elements.Select(e => Type(e, depth))] },
            UnionType u => u with { Types = [.. u.Types.Select(t => Type(t, depth))] },
            IntersectionType i => i with { Types = [.. i.Types.Select(t => Type(t, depth))] },
            ObjectType o => o with
            {
                Members = [.. o.Members.Select(Member)],
                IndexSignatures = [.. o.IndexSignatures.Select(x => x with { Type = Type(x.Type, depth) })],
                CallSignatures = [.. o.CallSignatures.Select(Signature)],
                ConstructSignatures = [.. o.ConstructSignatures.Select(Signature)],
            },
            FunctionType f => f with { Params = [.. f.Params.Select(p => p with { Type = Type(p.Type, depth) })], Returns = Type(f.Returns, depth) },
            _ => type,
        };

        /// <summary>The alias's type with its type parameters replaced by the arguments.</summary>
        private static IrType Substitute(IrType type, Dictionary<string, IrType> args) => type switch
        {
            TypeParameterType t when args.TryGetValue(t.Name, out var arg) => arg,
            RefType r => r with { Args = [.. r.Args.Select(a => Substitute(a, args))] },
            ArrayType a => a with { Element = Substitute(a.Element, args) },
            TupleType t => t with { Elements = [.. t.Elements.Select(e => Substitute(e, args))] },
            UnionType u => u with { Types = [.. u.Types.Select(t => Substitute(t, args))] },
            IntersectionType i => i with { Types = [.. i.Types.Select(t => Substitute(t, args))] },
            FunctionType f => f with { Params = [.. f.Params.Select(p => p with { Type = Substitute(p.Type, args) })], Returns = Substitute(f.Returns, args) },
            _ => type, // an object type's members keep the parameter: the use is skipped, as a generic object type is
        };
    }
}
