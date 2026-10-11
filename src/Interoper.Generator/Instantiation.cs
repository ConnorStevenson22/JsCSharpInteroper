using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// Mapping §2: a use of a generic union alias (<c>OneOrMany&lt;Marker&gt;</c>, where
/// <c>type OneOrMany&lt;T&gt; = T | T[]</c>), or of a generic alias of one, is the union it stands for, with the type
/// arguments in place of the type parameters (a missing one is its default), as if it were written there: an anonymous
/// union named after its owner (R27). A C# union struct isn't generic, so the alias has no type of its own.
/// </summary>
internal static class Instantiation
{
    /// <summary>How deep an alias may expand into others, so a recursive one stops (it's left as it is, and skipped).</summary>
    private const int MaxDepth = 8;

    /// <summary>The IR with every use of a generic union alias replaced by its instantiated union.</summary>
    public static IrDocument Instantiate(IrDocument ir)
    {
        var generic = ir.Modules.SelectMany(m => m.Declarations.OfType<AliasDeclaration>().Where(a => a.TypeParameters.Count > 0)
            .Select(a => (Key: (m.Specifier, a.Name), Alias: a))).ToDictionary(x => x.Key, x => x.Alias);
        // The union aliases, and the aliases of those (type Same<V> = OneOrMany<V>), transitively.
        var aliases = generic.Where(x => x.Value.Type is UnionType).ToDictionary();
        for (var added = true; added;)
        {
            added = false;
            foreach (var (key, alias) in generic)
            {
                if (!aliases.ContainsKey(key) && alias.Type is RefType { Module: { } module } r && aliases.ContainsKey((module, r.Name)))
                {
                    aliases[key] = alias;
                    added = true;
                }
            }
        }
        if (aliases.Count == 0)
        {
            return ir;
        }

        IrType? Expand(IrType type, int depth)
        {
            if (depth >= MaxDepth || type is not RefType { Module: { } module } r || !aliases.TryGetValue((module, r.Name), out var alias) ||
                r.Args.Count > alias.TypeParameters.Count || alias.TypeParameters.Skip(r.Args.Count).Any(t => t.Default is null))
            {
                return null;
            }
            // Raw arguments, then defaults (which may name earlier parameters); the result is walked once, a level deeper.
            var args = new Dictionary<string, IrType>();
            foreach (var (parameter, i) in alias.TypeParameters.Select((t, i) => (t, i)))
            {
                args[parameter.Name] = i < r.Args.Count ? r.Args[i] : Map(parameter.Default!, t => Substitute(t, args));
            }
            return Walk(Map(alias.Type, t => Substitute(t, args)), depth + 1);
        }
        IrType Walk(IrType type, int depth) => Map(type, t => Expand(t, depth));

        return ir with
        {
            Modules = [.. ir.Modules.Select(m => m with { Declarations = [.. m.Declarations.Select(d => Declaration(d, t => Expand(t, 0)))] })],
            Elements = [.. ir.Elements.Select(e => Element(e, t => Expand(t, 0)))],
        };
    }

    private static IrType? Substitute(IrType type, Dictionary<string, IrType> args) =>
        type is TypeParameterType p && args.TryGetValue(p.Name, out var arg) ? arg : null;

    /// <summary>
    /// A type with <paramref name="replace"/> applied, from the outside in: where it gives a type, that's the result;
    /// otherwise the type's parts are mapped, object members and signatures included.
    /// </summary>
    private static IrType Map(IrType type, Func<IrType, IrType?> replace) => replace(type) ?? type switch
    {
        RefType r => r with { Args = [.. r.Args.Select(a => Map(a, replace))] },
        ArrayType a => a with { Element = Map(a.Element, replace) },
        TupleType t => t with { Elements = [.. t.Elements.Select(e => Map(e, replace))] },
        UnionType u => u with { Types = [.. u.Types.Select(t => Map(t, replace))] },
        IntersectionType i => i with { Types = [.. i.Types.Select(t => Map(t, replace))] },
        ObjectType o => o with
        {
            Members = [.. o.Members.Select(m => Member(m, replace))],
            IndexSignatures = [.. o.IndexSignatures.Select(x => x with { Type = Map(x.Type, replace) })],
            CallSignatures = [.. o.CallSignatures.Select(s => Signature(s, replace))],
            ConstructSignatures = [.. o.ConstructSignatures.Select(s => Signature(s, replace))],
        },
        FunctionType f => f with { Params = [.. f.Params.Select(p => p with { Type = Map(p.Type, replace) })], Returns = Map(f.Returns, replace) },
        _ => type,
    };

    private static Member Member(Member m, Func<IrType, IrType?> replace) => m switch
    {
        PropertyMember p => p with { Type = Map(p.Type, replace), WriteType = p.WriteType is { } w ? Map(w, replace) : null },
        MethodMember method => method with { Signatures = [.. method.Signatures.Select(s => Signature(s, replace))] },
        _ => m,
    };

    private static Signature Signature(Signature s, Func<IrType, IrType?> replace) => s with
    {
        Params = [.. s.Params.Select(p => p with { Type = Map(p.Type, replace) })],
        Returns = s.Returns is { } r ? Map(r, replace) : null,
    };

    private static Declaration Declaration(Declaration d, Func<IrType, IrType?> replace) => d switch
    {
        ClassDeclaration c => c with
        {
            Extends = c.Extends is { } e ? Map(e, replace) : null,
            Implements = [.. c.Implements.Select(t => Map(t, replace))],
            Constructors = [.. c.Constructors.Select(s => Signature(s, replace))],
            Members = [.. c.Members.Select(m => Member(m, replace))],
            Events = [.. c.Events.Select(e => e with { Type = Map(e.Type, replace) })],
        },
        InterfaceDeclaration i => i with
        {
            Extends = [.. i.Extends.Select(t => Map(t, replace))],
            Members = [.. i.Members.Select(m => Member(m, replace))],
            IndexSignatures = [.. i.IndexSignatures.Select(x => x with { Type = Map(x.Type, replace) })],
            CallSignatures = [.. i.CallSignatures.Select(s => Signature(s, replace))],
            ConstructSignatures = [.. i.ConstructSignatures.Select(s => Signature(s, replace))],
        },
        AliasDeclaration a => a with { Type = Map(a.Type, replace) },
        FunctionDeclaration f => f with { Signatures = [.. f.Signatures.Select(s => Signature(s, replace))] },
        VariableDeclaration v => v with { Type = Map(v.Type, replace) },
        _ => d,
    };

    private static Element Element(Element e, Func<IrType, IrType?> replace) => e with
    {
        Attributes = [.. e.Attributes.Select(a => a with { Type = a.Type is { } t ? Map(t, replace) : null })],
        Events = [.. e.Events.Select(x => x with { Detail = x.Detail is { } d ? Map(d, replace) : null, Type = x.Type is { } t ? Map(t, replace) : null })],
    };
}
