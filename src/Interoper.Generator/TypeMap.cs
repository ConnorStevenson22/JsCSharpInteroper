using System.Text.Json;
using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>A member of a data or snapshot record, as it is on the wire.</summary>
/// <param name="Constant">The value of a member typed with one literal: always written, and no property (R16, R20).</param>
/// <param name="Opt">The property is <c>Opt&lt;T&gt;</c> (R3); a union never is (R2).</param>
/// <param name="Inherited">Declared by the base snapshot record (R16).</param>
internal sealed record Field(string JsName, string Name, CsType? Type, bool Required, bool Opt = false, JsonElement? Constant = null, bool Inherited = false)
{
    public string TypeName => Opt ? $"Opt<{Type!.Name}>" : Type!.Name;
}

/// <summary>An enum or union struct: what a union type becomes (or a TS enum).</summary>
internal abstract record DataType(string Name);

/// <summary>A literal union (or TS enum) as an enum: string values with a name map (R9), or more than 64 or open-ended
/// as an extensible enum (R10), or numbers with explicit values.</summary>
internal sealed record EnumType(string Name, IReadOnlyList<(string Name, JsonElement Value)> Members, bool Extensible) : DataType(Name)
{
    public bool IsNumber => Members.Count > 0 && Members[0].Value.ValueKind == JsonValueKind.Number;
}

/// <summary>A generated union struct (R2). <see cref="Discriminant"/> is the property its object cases are told apart by (R19 step 2).</summary>
internal sealed record UnionStruct(string Name, IReadOnlyList<UnionCase> Cases, string? Discriminant) : DataType(Name);

/// <param name="Declaration">What an object case is read as (R19 steps 2 and 3).</param>
internal sealed record UnionCase(CsType Type, Declaration? Declaration);

/// <summary>
/// How the selected declarations map to C#: the type of each IR type, the fields of each record, and the enums and unions
/// that unions become, including the anonymous ones named after their owner (R27), which it collects in <see cref="Owned"/>.
/// </summary>
internal sealed class TypeMap
{
    private const int MaxEnumMembers = 64; // R10

    private readonly Dictionary<(string Module, string Name), Declaration> declarations = [];
    private readonly List<ClassDeclaration> classes = [];
    private readonly HashSet<string> valueClasses;
    private readonly ICollection<Diagnostic> diagnostics;
    private readonly Dictionary<string, DataType?> unions = [];
    private readonly Dictionary<Declaration, IReadOnlyList<Field>> fields = new(ReferenceEqualityComparer.Instance);
    private readonly List<DataType> owned = [];

    /// <summary>Selected declarations stand for themselves (a class trimmed to its included members).</summary>
    public TypeMap(IrDocument ir, Profile profile, Selection selection, ICollection<Diagnostic> diagnostics)
    {
        foreach (var module in ir.Modules)
        {
            foreach (var declaration in module.Declarations)
            {
                declarations[(module.Specifier, declaration.Name)] = declaration;
            }
        }
        foreach (var selected in selection.Declarations)
        {
            declarations[(selected.Module, selected.Declaration.Name)] = selected.Declaration;
            if (selected.Declaration is ClassDeclaration c)
            {
                classes.Add(c);
            }
        }
        valueClasses = [.. profile.ValueClasses];
        this.diagnostics = diagnostics;
        Registry = Naming.Type(ir.Package.Name.Split('/')[^1]);
    }

    /// <summary>The generated registry class, named after the package without its scope (<c>DemoMaps</c>).</summary>
    public string Registry { get; }

    /// <summary>Anonymous enums and unions named after their owner (R27), in the order they were met.</summary>
    public IReadOnlyList<DataType> Owned => owned;

    public static string SnapshotName(ClassDeclaration c) => Naming.Type(c.Name) + "Snapshot";

    public bool IsValueLike(ClassDeclaration c) => valueClasses.Contains(c.Name);

    /// <summary>R16: the snapshots of an abstract value-like class form a closed hierarchy.</summary>
    public bool IsClosed(ClassDeclaration c) => c.Abstract && IsValueLike(c);

    /// <summary>The closed snapshot record this class's snapshot derives from.</summary>
    public ClassDeclaration? SnapshotBase(ClassDeclaration c) =>
        c.Extends is RefType r && Find(r) is ClassDeclaration b && classes.Contains(b) && IsClosed(b) ? b : null;

    /// <summary>The emitted concrete classes whose snapshots derive from <paramref name="c"/>'s, in IR order.</summary>
    public IReadOnlyList<ClassDeclaration> ConcreteSubclasses(ClassDeclaration c) =>
        [.. classes.Where(sub => !sub.Abstract && Bases(sub).Contains(c))];

    private IEnumerable<ClassDeclaration> Bases(ClassDeclaration c)
    {
        for (var b = SnapshotBase(c); b is not null; b = SnapshotBase(b))
        {
            yield return b;
        }
    }

    /// <summary>The string constant every concrete subclass of a closed base declares: <c>type</c> (R19 step 2).</summary>
    public string Discriminant(ClassDeclaration c)
    {
        var subclasses = ConcreteSubclasses(c).Select(Fields).ToList();
        return subclasses.FirstOrDefault()?.Where(IsStringConstant).Select(f => f.JsName)
                .FirstOrDefault(name => subclasses.All(s => s.Any(f => f.JsName == name && IsStringConstant(f))))
            ?? throw new NotSupportedException($"{c.Name}: its subclasses have no string discriminant to read it by (R19, IOP2003).");

        static bool IsStringConstant(Field f) => f.Constant is { ValueKind: JsonValueKind.String };
    }

    // --- Types ---

    /// <summary>The C# type of a non-nullable position; <paramref name="owner"/> names an anonymous union there (R27).</summary>
    public CsType Map(IrType type, string owner) => type switch
    {
        PrimitiveType { Name: "number" } => CsType.Number,
        PrimitiveType { Name: "string" } => CsType.String,
        PrimitiveType { Name: "boolean" } => CsType.Boolean,
        RefType { Module: not null } r when Find(r) is { } declaration => Named(declaration),
        ArrayType a => CsType.Array(Map(a.Element, owner)),
        TupleType { Required: null, Rest: false, Elements.Count: > 0 } t
            when t.Elements.Select(e => Map(e, owner).Name).Distinct().Count() == 1 => CsType.Array(Map(t.Elements[0], owner)), // R5
        UnionType u when Union(owner, u) is { } union => Own(union),
        _ => throw NotSupported(owner, $"the IR type {type.GetType().Name}"),
    };

    /// <summary>
    /// The type of a member or parameter: without null and undefined, which make it nullable. A nullable type is
    /// <c>Opt&lt;T&gt;</c> (R3), unless it's a union, which has those states itself (R2).
    /// </summary>
    public (CsType Type, bool Nullable) Slot(IrType type, string owner)
    {
        var nullable = false;
        if (type is UnionType u && u.Types.Any(IsNullish))
        {
            var rest = u.Types.Where(t => !IsNullish(t)).ToList();
            type = rest.Count == 1 ? rest[0] : u with { Types = rest };
            nullable = true;
        }
        return (Map(type, owner), nullable || Flatten(type).Any(IsNullish));
    }

    private CsType Named(Declaration declaration) => declaration switch
    {
        InterfaceDeclaration or AliasDeclaration { Type: ObjectType } => CsType.Record(Naming.Type(declaration.Name)),
        ClassDeclaration c when IsValueLike(c) => CsType.Record(SnapshotName(c)), // R16
        EnumDeclaration e => TypeOf(Enum(e)),
        AliasDeclaration a when Union(Naming.Type(a.Name), a.Type) is { } union => TypeOf(union),
        AliasDeclaration a => Map(Flatten(a.Type).Single(t => !IsNullish(t)), Naming.Type(a.Name)),
        _ => throw NotSupported(declaration.Name, "an entity in a data position (R17)"),
    };

    private static CsType TypeOf(DataType type) => type switch
    {
        EnumType { Extensible: true } e => CsType.Method(e.Name, JsonKind.String, $"{e.Name}.Write", $"{e.Name}.Read", isValueType: true),
        EnumType { IsNumber: true } e => CsType.NumberEnum(e.Name),
        EnumType e => CsType.Method(e.Name, JsonKind.String, $"{e.Name}Names.Write", $"{e.Name}Names.Read", isValueType: true),
        _ => CsType.Union(((UnionStruct)type).Name),
    };

    private CsType Own(DataType type)
    {
        if (!owned.Contains(type))
        {
            owned.Add(type);
        }
        return TypeOf(type);
    }

    // --- Unions ---

    /// <summary>
    /// What a union named <paramref name="name"/> becomes, flattened (R6) and without null and undefined; null when
    /// one type is left.
    /// </summary>
    public DataType? Union(string name, IrType type)
    {
        if (unions.TryGetValue(name, out var known))
        {
            return known;
        }
        var flat = Flatten(type).Where(t => !IsNullish(t)).ToList();
        var literals = flat.OfType<LiteralType>().Select(l => l.Value).ToList();
        DataType? result =
            flat.Count < 2 ? null
            : flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.String } || IsOpenString(t)) ? new EnumType(name,
                EnumMembers(name, literals, literals.Select(v => v.GetString()!)), literals.Count > MaxEnumMembers || flat.Any(IsOpenString))
            : flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.Number }) ? new EnumType(name,
                EnumMembers(name, literals, literals.Select(v => v.GetRawText())), Extensible: false)
            : new UnionStruct(name, Cases(name, flat),
                type is UnionType u && u.Types.Count(t => !IsNullish(t)) == flat.Count ? u.Discriminant : null);
        unions[name] = result;
        return result;
    }

    public EnumType Enum(EnumDeclaration e)
    {
        var name = Naming.Type(e.Name);
        if (unions.TryGetValue(name, out var known))
        {
            return (EnumType)known!;
        }
        var values = e.Members.Select(m => m.Value).ToList();
        if (values.Select(v => v.ValueKind).Distinct().Count() > 1)
        {
            throw NotSupported(e.Name, "an enum of strings and numbers");
        }
        var result = new EnumType(name, EnumMembers(name, values, e.Members.Select(m => m.Name)), Extensible: false);
        unions[name] = result;
        return result;
    }

    /// <summary>R25: names for the values; numbers must be integers.</summary>
    private List<(string, JsonElement)> EnumMembers(string type, List<JsonElement> values, IEnumerable<string> names)
    {
        if (values.FirstOrDefault(v => v.ValueKind == JsonValueKind.Number && !v.TryGetInt32(out _)) is { ValueKind: JsonValueKind.Number } value)
        {
            throw NotSupported(type, $"the non-integer enum value {value}");
        }
        return [.. Naming.EnumMembers(names, type, diagnostics).Zip(values)];
    }

    /// <summary>The cases of a union, after mapping (R5: one per C# type).</summary>
    private List<UnionCase> Cases(string name, List<IrType> types)
    {
        var cases = new List<UnionCase>();
        foreach (var type in types)
        {
            var mapped = type is LiteralType ? throw NotSupported(name, "a literal in a union of other types") : Map(type, name);
            if (mapped.IsValueType)
            {
                throw NotSupported(name, $"the value-type case {mapped.Name} (R2 keeps unions non-boxing)");
            }
            if (cases.All(c => c.Type.Name != mapped.Name))
            {
                cases.Add(new(mapped, type is RefType r ? Find(r) : null));
            }
        }
        return cases;
    }

    /// <summary>R6: a union's members, with the members of union aliases in their place.</summary>
    private IEnumerable<IrType> Flatten(IrType type) => type switch
    {
        UnionType u => u.Types.SelectMany(Flatten),
        RefType { Module: not null } r when Find(r) is AliasDeclaration { Type: UnionType } a => Flatten(a.Type),
        _ => [type],
    };

    /// <summary>R10: <c>string &amp; {}</c>, which keeps a literal union open-ended.</summary>
    private static bool IsOpenString(IrType type) =>
        type is IntersectionType { Types.Count: 2 } i && i.Types.Contains(new PrimitiveType("string")) &&
        i.Types.Any(t => t is ObjectType { Members.Count: 0, IndexSignatures.Count: 0, CallSignatures.Count: 0, ConstructSignatures.Count: 0 });

    private static bool IsNullish(IrType type) => type is PrimitiveType { Name: "null" or "undefined" };

    // --- Records ---

    /// <summary>
    /// The wire members of a data record (R15: an interface with its bases, or an alias of an object type) or of a
    /// class's snapshot record (R18: its writable data members, with the getter's type, all optional; R17: no entities).
    /// A member typed with one literal is a constant (R20).
    /// </summary>
    public IReadOnlyList<Field> Fields(Declaration declaration)
    {
        if (fields.TryGetValue(declaration, out var known))
        {
            return known;
        }
        var name = Naming.Type(declaration.Name);
        var result = declaration switch
        {
            InterfaceDeclaration i when i.IndexSignatures.Count == 0 => DataFields(name, Members(i)),
            AliasDeclaration { Type: ObjectType { IndexSignatures.Count: 0 } o } => DataFields(name, o.Members),
            ClassDeclaration c => SnapshotFields(c),
            _ => throw NotSupported(declaration.Name, "an index signature"),
        };
        fields[declaration] = result;
        return result;
    }

    /// <summary>The wire members of an object case, and whether each is required, for R19 step 3.</summary>
    public IEnumerable<(string Name, bool Required)> WireMembers(Declaration declaration)
    {
        var wire = Fields(declaration).Select(f => (f.JsName, f.Required || f.Constant is not null));
        return declaration is ClassDeclaration c && IsClosed(c) ? wire.Prepend((Discriminant(c), true)) : wire;
    }

    /// <summary>Maps the types of a class's properties, so the anonymous ones its live members use are emitted (R27).</summary>
    public void MapProperties(ClassDeclaration c)
    {
        foreach (var p in c.Members.OfType<PropertyMember>().Where(p => !p.Static && p.Type is not LiteralType && !UsesEntity(p.Type)))
        {
            Slot(p.Type, Naming.OwnedType(Naming.Type(c.Name), p.Name));
            if (p.WriteType is { } writeType && !UsesEntity(writeType))
            {
                Slot(writeType, Naming.OwnedType(Naming.Type(c.Name), p.Name, input: true));
            }
        }
    }

    /// <summary>An interface's own members, then its bases' (as the IR orders inherited members).</summary>
    private IEnumerable<PropertyMember> Members(InterfaceDeclaration i)
    {
        var members = i.Members.OfType<PropertyMember>().ToList();
        foreach (var b in i.Extends.OfType<RefType>().Select(Find).OfType<InterfaceDeclaration>())
        {
            members.AddRange(Members(b).Where(m => members.All(x => x.Name != m.Name)));
        }
        return members;
    }

    private List<Field> DataFields(string type, IEnumerable<Member> members)
    {
        var taken = new HashSet<string> { type, "Write", "Read" };
        var result = new List<Field>();
        foreach (var p in members.OfType<PropertyMember>().Where(p => !p.Static))
        {
            if (p.Type is LiteralType constant)
            {
                result.Add(new(p.Name, "", null, Required: true, Constant: constant.Value));
                continue;
            }
            var (mapped, nullable) = Slot(p.Type, Naming.OwnedType(type, p.Name));
            var name = Naming.Member(taken, Naming.Pascal(p.Name), $"{type}.{p.Name}", diagnostics);
            result.Add(new(p.Name, name, mapped, Required: !p.Optional, Opt: (nullable || p.Optional) && !mapped.IsUnion));
        }
        return result;
    }

    private List<Field> SnapshotFields(ClassDeclaration c)
    {
        var type = Naming.Type(c.Name);
        var result = SnapshotBase(c) is { } b ? Fields(b).Select(f => f with { Inherited = true }).ToList() : [];
        var taken = new HashSet<string>(result.Select(f => f.Name)) { SnapshotName(c), "Write", "WriteMembers", "Read" };
        foreach (var p in c.Members.OfType<PropertyMember>().Where(p => !p.Static))
        {
            if (p.Type is LiteralType constant)
            {
                result.Add(new(p.Name, "", null, Required: true, Constant: constant.Value));
            }
            else if (!p.Readonly && !UsesEntity(p.Type))
            {
                var mapped = Slot(p.Type, Naming.OwnedType(type, p.Name)).Type;
                var name = Naming.Member(taken, Naming.Pascal(p.Name), $"{SnapshotName(c)}.{p.Name}", diagnostics);
                result.Add(new(p.Name, name, mapped, Required: false, Opt: !mapped.IsUnion));
            }
        }
        return result;
    }

    /// <summary>Whether a type mentions an entity class, which only a scope can read (R17).</summary>
    private bool UsesEntity(IrType type) => type switch
    {
        RefType { Module: not null } r => Find(r) switch
        {
            ClassDeclaration c => !IsValueLike(c),
            AliasDeclaration a => UsesEntity(a.Type),
            _ => false,
        },
        UnionType u => u.Types.Any(UsesEntity),
        ArrayType a => UsesEntity(a.Element),
        TupleType t => t.Elements.Any(UsesEntity),
        _ => false,
    };

    private Declaration? Find(RefType type) => type.Module is { } module ? declarations.GetValueOrDefault((module, type.Name)) : null;

    private static NotSupportedException NotSupported(string subject, string what) =>
        new($"{subject}: {what} isn't supported yet.");
}
