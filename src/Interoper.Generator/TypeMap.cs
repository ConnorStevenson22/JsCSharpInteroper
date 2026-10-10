using System.Diagnostics;
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
internal abstract record DataType(TypeName Type)
{
    public string Name => Type.Name;
}

/// <summary>A literal union (or TS enum) as an enum: string values with a name map (R9), or more than 64 or open-ended
/// as an extensible enum (R10), or numbers with explicit values.</summary>
internal sealed record EnumType(TypeName Type, IReadOnlyList<(string Name, JsonElement Value)> Members, bool Extensible) : DataType(Type)
{
    public bool IsNumber => Members.Count > 0 && Members[0].Value.ValueKind == JsonValueKind.Number;
}

/// <summary>A generated union struct (R2). <see cref="Discriminant"/> is the property its object cases are told apart by (R19 step 2).</summary>
internal sealed record UnionStruct(TypeName Type, IReadOnlyList<UnionCase> Cases, string? Discriminant) : DataType(Type);

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
    private readonly Dictionary<Declaration, string> moduleOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ClassDeclaration, string> ids = new(ReferenceEqualityComparer.Instance);
    private readonly string package, root;
    private readonly List<ClassDeclaration> classes = [];
    private readonly HashSet<string> valueClasses;
    private readonly ICollection<Diagnostic> diagnostics;
    private readonly Dictionary<TypeName, DataType?> unions = [];
    private readonly Dictionary<Declaration, IReadOnlyList<Field>> fields = new(ReferenceEqualityComparer.Instance);
    private readonly List<DataType> owned = [];
    private readonly HashSet<ClassDeclaration> watchable = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// <paramref name="selected"/> stand for themselves (a class trimmed to what can be mapped); its classes are the
    /// live classes to emit. <paramref name="ns"/> is the root namespace (R27); the mapping checks don't need it.
    /// </summary>
    public TypeMap(IrDocument ir, Profile profile, IEnumerable<Selected> selected, ICollection<Diagnostic> diagnostics, string ns = "")
    {
        foreach (var module in ir.Modules)
        {
            foreach (var declaration in module.Declarations)
            {
                declarations[(module.Specifier, declaration.Name)] = declaration;
                moduleOf[declaration] = module.Specifier;
            }
        }
        foreach (var s in selected)
        {
            // §5: a class whose own or inherited methods include the profile's watch method has watches.
            if (s.Declaration is ClassDeclaration c && profile.Watch is { } watch &&
                Ancestors(declarations.GetValueOrDefault((s.Module, c.Name)) as ClassDeclaration).Any(k => k.Members.Any(m => m is MethodMember && m.Name == watch)))
            {
                watchable.Add(c);
            }
        }
        foreach (var s in selected)
        {
            declarations[(s.Module, s.Declaration.Name)] = s.Declaration;
            moduleOf[s.Declaration] = s.Module;
            if (s.Declaration is ClassDeclaration c)
            {
                classes.Add(c);
            }
        }
        valueClasses = [.. profile.ValueClasses];
        this.diagnostics = diagnostics;
        (package, root) = (ir.Package.Name, ns);
        Registry = Naming.Type(ir.Package.Name.Split('/')[^1]);
        // Classes of one name in different namespaces need different class id constants: the later ones get a suffix.
        var taken = new HashSet<string>();
        foreach (var c in classes)
        {
            var (wanted, id) = (Naming.Type(c.Name), Naming.Type(c.Name));
            for (var n = 2; !taken.Add(id); n++)
            {
                id = $"{wanted}{n}";
            }
            if (id != wanted)
            {
                diagnostics.Add(new(Diagnostic.Renamed, $"{moduleOf[c]} {c.Name}", $"its class id is {id}Id: {wanted}Id is taken (R27)"));
            }
            ids[c] = id + "Id";
        }
    }

    /// <summary>The generated registry class, named after the package without its scope (<c>DemoMaps</c>), in the root namespace.</summary>
    public string Registry { get; }

    /// <summary>R27: the namespace of a declaration's types: the root namespace plus its module's folders.</summary>
    public string Namespace(Declaration declaration) => Namespace(moduleOf[declaration]);

    /// <summary>R27: the namespace of a module's types: the root namespace plus its folders.</summary>
    public string Namespace(string module) => Naming.Namespace(root, package, module);

    /// <summary>The type a declaration becomes, in its namespace (R27).</summary>
    public TypeName Name(Declaration declaration) => new(Namespace(declaration), Naming.Type(declaration.Name));

    /// <summary>A class's snapshot record (R18), in the class's namespace.</summary>
    public TypeName Snapshot(ClassDeclaration c) => new(Namespace(c), SnapshotName(c));

    /// <summary>The constant holding a live class's class id (design §2), as code in namespace <paramref name="ns"/> names it.</summary>
    public string ClassId(ClassDeclaration c, string ns) => $"{new TypeName(root, Registry).In(ns)}.{ClassIdName(c)}";

    /// <summary>The name of that constant in the registry: <c>PointId</c>.</summary>
    public string ClassIdName(ClassDeclaration c) => ids[c];

    /// <summary>Anonymous enums and unions named after their owner (R27), in the order they were met.</summary>
    public IReadOnlyList<DataType> Owned => owned;

    public static string SnapshotName(ClassDeclaration c) => Naming.Type(c.Name) + "Snapshot";

    public bool IsValueLike(ClassDeclaration c) => valueClasses.Contains(c.Name);

    /// <summary>R16: the snapshots of an abstract value-like class form a closed hierarchy.</summary>
    public bool IsClosed(ClassDeclaration c) => c.Abstract && IsValueLike(c);

    /// <summary>The emitted live classes, in IR order.</summary>
    public IReadOnlyList<ClassDeclaration> Classes => classes;

    /// <summary>§5: whether the class has the profile's watch method, its own or a base's, so each of its properties gets <c>WatchX</c>.</summary>
    public bool HasWatches(ClassDeclaration c) => watchable.Contains(c);

    /// <summary>The emitted class this class's live class derives from; otherwise it derives from <c>JsObject</c>.</summary>
    public ClassDeclaration? LiveBase(ClassDeclaration c) =>
        c.Extends is RefType r && Find(r) is ClassDeclaration b && classes.Contains(b) ? b : null;

    /// <summary>The closed snapshot record this class's snapshot derives from.</summary>
    public ClassDeclaration? SnapshotBase(ClassDeclaration c) => LiveBase(c) is { } b && IsClosed(b) ? b : null;

    /// <summary>The emitted concrete classes whose snapshots derive from <paramref name="c"/>'s, in IR order.</summary>
    public IReadOnlyList<ClassDeclaration> ConcreteSubclasses(ClassDeclaration c) =>
        [.. classes.Where(sub => !sub.Abstract && Bases(sub).Contains(c))];

    public bool HasSubclasses(ClassDeclaration c) => classes.Any(sub => LiveBase(sub) == c);

    private IEnumerable<ClassDeclaration> Bases(ClassDeclaration c)
    {
        for (var b = SnapshotBase(c); b is not null; b = SnapshotBase(b))
        {
            yield return b;
        }
    }

    /// <summary>The string constant every concrete subclass of a closed base declares: <c>type</c> (R19 step 2).</summary>
    public string Discriminant(ClassDeclaration c) => DiscriminantOrNull(c) ?? throw Unreachable(c.Name);

    private string? DiscriminantOrNull(ClassDeclaration c)
    {
        var subclasses = ConcreteSubclasses(c).Select(StringConstants).ToList();
        return subclasses.FirstOrDefault()?.FirstOrDefault(name => subclasses.All(s => s.Contains(name)));
    }

    // --- Types ---

    /// <summary>The C# type of a non-nullable position; <paramref name="owner"/> names an anonymous union there (R27).</summary>
    public CsType Map(IrType type, TypeName owner) => type switch
    {
        PrimitiveType { Name: "number" } => CsType.Number,
        PrimitiveType { Name: "string" } => CsType.String,
        PrimitiveType { Name: "boolean" } => CsType.Boolean,
        RefType { Module: not null } r when Find(r) is { } declaration => Named(declaration),
        ArrayType a => CsType.Array(Map(a.Element, owner)),
        TupleType { Required: null, Rest: false, Elements.Count: > 0 } t
            when t.Elements.Select(e => Map(e, owner).Name).Distinct().Count() == 1 => CsType.Array(Map(t.Elements[0], owner)), // R5
        UnionType u when Union(owner, u) is { } union => Own(union),
        _ => throw Unreachable(owner.Name),
    };

    /// <summary>
    /// The type of a member or parameter: without null and undefined, which make it nullable. A nullable type is
    /// <c>Opt&lt;T&gt;</c> (R3), unless it's a union, which has those states itself (R2). A member of a live class
    /// (<paramref name="self"/>) can also be an entity or <c>this</c>: a live object (R16, R17).
    /// </summary>
    public (CsType Type, bool Nullable) Slot(IrType type, TypeName owner, TypeName? self = null)
    {
        var nullable = false;
        if (type is UnionType u && u.Types.Any(IsNullish))
        {
            var rest = u.Types.Where(t => !IsNullish(t)).ToList();
            type = rest.Count == 1 ? rest[0] : u with { Types = rest };
            nullable = true;
        }
        var mapped = self is null ? null : type switch
        {
            ThisType => CsType.Live(self),
            RefType { Module: not null } r when Find(r) is ClassDeclaration c && !IsValueLike(c) => CsType.Live(Name(c)),
            _ => null,
        };
        return (mapped ?? Map(type, owner), nullable || Flatten(type).Any(IsNullish));
    }

    /// <summary>Whether a result mentions a value-like class, so it's read by value (R16, design §3.4 <c>byValue</c>).</summary>
    public bool ByValue(IrType type) => Mentions(type, IsValueLike);

    private CsType Named(Declaration declaration) => declaration switch
    {
        InterfaceDeclaration or AliasDeclaration { Type: ObjectType } => CsType.Record(Name(declaration)),
        ClassDeclaration c when IsValueLike(c) => CsType.Record(Snapshot(c)), // R16
        EnumDeclaration e => TypeOf(Enum(e)),
        AliasDeclaration a when Union(Name(a), a.Type) is { } union => TypeOf(union),
        AliasDeclaration a => Map(Flatten(a.Type).Single(t => !IsNullish(t)), Name(a)),
        _ => throw Unreachable(declaration.Name),
    };

    private static CsType TypeOf(DataType type) => type switch
    {
        EnumType { Extensible: true } e => CsType.Extensible(e.Type),
        EnumType { IsNumber: true } e => CsType.NumberEnum(e.Type),
        EnumType e => CsType.StringEnum(e.Type),
        _ => CsType.Union(type.Type),
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
    public DataType? Union(TypeName name, IrType type)
    {
        if (unions.TryGetValue(name, out var known))
        {
            return known;
        }
        var flat = Values(type);
        var literals = flat.OfType<LiteralType>().Select(l => l.Value).ToList();
        DataType? result =
            flat.Count < 2 ? null
            : flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.String } || IsOpenString(t)) ? new EnumType(name,
                EnumMembers(name.Name, literals, literals.Select(v => v.GetString()!)), literals.Count > MaxEnumMembers || flat.Any(IsOpenString))
            : flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.Number }) ? new EnumType(name,
                EnumMembers(name.Name, literals, literals.Select(v => v.GetRawText())), Extensible: false)
            : new UnionStruct(name, Cases(name, flat),
                type is UnionType u && u.Types.Count(t => !IsNullish(t)) == flat.Count ? u.Discriminant : null);
        unions[name] = result;
        return result;
    }

    public EnumType Enum(EnumDeclaration e)
    {
        var name = Name(e);
        if (unions.TryGetValue(name, out var known))
        {
            return (EnumType)known!;
        }
        var values = e.Members.Select(m => m.Value).ToList();
        var result = new EnumType(name, EnumMembers(name.Name, values, e.Members.Select(m => m.Name)), Extensible: false);
        unions[name] = result;
        return result;
    }

    /// <summary>R25: names for the values.</summary>
    private List<(string, JsonElement)> EnumMembers(string type, List<JsonElement> values, IEnumerable<string> names) =>
        [.. Naming.EnumMembers(names, type, diagnostics).Zip(values)];

    /// <summary>The cases of a union, after mapping (R5: one per C# type).</summary>
    private List<UnionCase> Cases(TypeName name, List<IrType> types)
    {
        var cases = new List<UnionCase>();
        foreach (var type in types)
        {
            var mapped = Map(type, name);
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

    /// <summary>A union's members after R6, without null and undefined.</summary>
    private List<IrType> Values(IrType type) => [.. Flatten(type).Where(t => !IsNullish(t))];

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
        var result = declaration switch
        {
            InterfaceDeclaration i when i.IndexSignatures.Count == 0 => DataFields(Name(declaration), Members(i)),
            AliasDeclaration { Type: ObjectType { IndexSignatures.Count: 0 } o } => DataFields(Name(declaration), o.Members),
            ClassDeclaration c => SnapshotFields(c),
            _ => throw Unreachable(declaration.Name),
        };
        fields[declaration] = result;
        return result;
    }

    /// <summary>The value of a string constant: a case's discriminant (R19 step 2).</summary>
    public string Constant(Declaration declaration, string name) =>
        Fields(declaration).Single(f => f.JsName == name).Constant!.Value.GetString()!;

    /// <summary>The wire members of an object case, and whether each is required, for R19 step 3.</summary>
    public IEnumerable<(string Name, bool Required)> WireMembers(Declaration declaration)
    {
        var wire = Fields(declaration).Select(f => (f.JsName, f.Required));
        return declaration is ClassDeclaration c && IsClosed(c) ? wire.Prepend((Discriminant(c), true)) : wire;
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

    private List<Field> DataFields(TypeName type, IEnumerable<Member> members)
    {
        var taken = new HashSet<string> { type.Name, "Write", "Read" };
        var result = new List<Field>();
        foreach (var p in members.OfType<PropertyMember>().Where(p => !p.Static))
        {
            if (p.Type is LiteralType constant)
            {
                result.Add(new(p.Name, "", null, Required: true, Constant: constant.Value));
                continue;
            }
            var (mapped, nullable) = Slot(p.Type, type.Owned(p.Name));
            var name = Naming.Member(taken, Naming.Pascal(p.Name), $"{type.Name}.{p.Name}", diagnostics);
            result.Add(new(p.Name, name, mapped, Required: !p.Optional, Opt: (nullable || p.Optional) && !mapped.IsUnion));
        }
        return result;
    }

    /// <summary>A closed base's snapshot declares its members; any other emitted base's are repeated (R18).</summary>
    private List<Field> SnapshotFields(ClassDeclaration c)
    {
        var type = Name(c);
        var result = SnapshotBase(c) is { } b ? Fields(b).Select(f => f with { Inherited = true }).ToList() : [];
        var taken = new HashSet<string>(result.Select(f => f.Name)) { SnapshotName(c), "Write", "WriteMembers", "Read" };
        foreach (var p in c.Members.OfType<PropertyMember>().Where(p => !p.Static))
        {
            if (p.Type is LiteralType constant)
            {
                result.Add(new(p.Name, "", null, Required: true, Constant: constant.Value));
            }
            else if (IsSnapshotMember(p))
            {
                var mapped = Slot(p.Type, type.Owned(p.Name)).Type;
                var name = Naming.Member(taken, Naming.Pascal(p.Name), $"{SnapshotName(c)}.{p.Name}", diagnostics);
                result.Add(new(p.Name, name, mapped, Required: false, Opt: !mapped.IsUnion));
            }
        }
        if (SnapshotBase(c) is null && LiveBase(c) is { } live)
        {
            result.AddRange(Fields(live).Where(f => result.All(own => own.JsName != f.JsName)).Select(f => f with { Inherited = false }));
        }
        return result;
    }

    /// <summary>R18: a writable data member is in the snapshot; R17: one that mentions an entity isn't.</summary>
    private bool IsSnapshotMember(PropertyMember p) => !p.Static && p.Type is not LiteralType && !p.Readonly && !UsesEntity(p.Type);

    /// <summary>Whether a type mentions an entity class, which only a scope can read (R17).</summary>
    private bool UsesEntity(IrType type) => Mentions(type, c => !IsValueLike(c));

    private bool Mentions(IrType type, Func<ClassDeclaration, bool> match) => type switch
    {
        RefType { Module: not null } r => Find(r) switch
        {
            ClassDeclaration c => match(c),
            AliasDeclaration a => Mentions(a.Type, match),
            _ => false,
        },
        UnionType u => u.Types.Any(t => Mentions(t, match)),
        ArrayType a => Mentions(a.Element, match),
        TupleType t => t.Elements.Any(e => Mentions(e, match)),
        _ => false,
    };

    // --- What can't be mapped (mapping §8) ---
    //
    // These checks decide, without mapping anything, what Map, Union, Cases and the union reader (ValueTypeEmitter) would
    // refuse, so selection can skip it with what uses it. Every branch must mirror theirs; where it can't be exact, it
    // refuses more, never less. SelectionTests pairs each refused shape with an accepted one.

    /// <summary>
    /// Why a type can't be mapped where it is, or null: in a live class's member, parameter, result or payload
    /// (<paramref name="live"/>, where an entity or <c>this</c> is a live object), or in a data record or module
    /// function. It checks what <see cref="Slot"/> would refuse, but not the declarations the type names: selection
    /// checks each of those itself (<see cref="Problem(Declaration)"/>).
    /// </summary>
    public Unmappable? Problem(IrType type, bool live)
    {
        if (type is UnionType u && u.Types.Any(IsNullish))
        {
            var rest = u.Types.Where(t => !IsNullish(t)).ToList();
            if (rest.Count == 0)
            {
                return new("only `null` and `undefined`");
            }
            type = rest.Count == 1 ? rest[0] : u with { Types = rest };
        }
        var entity = type is ThisType || type is RefType { Module: not null } r && Find(r) is ClassDeclaration c && !IsValueLike(c);
        return live && entity ? null : MapProblem(type);
    }

    /// <summary>A result's, without its Promise (R8); <c>void</c> is none.</summary>
    public Unmappable? ResultProblem(IrType returns, bool live) => returns switch
    {
        RefType { Module: null, Name: "Promise", Args: [var inner] } => ResultProblem(inner, live),
        PrimitiveType { Name: "void" or "undefined" } => null,
        _ => Problem(returns, live),
    };

    /// <summary>A live class's property: its getter's and setter's types, and its snapshot member's (R18). A constant is fine (R20).</summary>
    public Unmappable? Problem(PropertyMember p) =>
        p.Type is LiteralType ? null
        : Problem(p.Type, live: true) ?? (p.WriteType is { } w ? Problem(w, live: true) : null) ?? (IsSnapshotMember(p) ? Problem(p.Type, live: false) : null);

    /// <summary>
    /// A declaration's own problem, or null: a data record's members (R15), an enum's values, what a union alias becomes
    /// (R2, R9, R19), and a closed base's discriminant (R19 step 2).
    /// </summary>
    public Unmappable? Problem(Declaration declaration) => declaration switch
    {
        InterfaceDeclaration i => RecordProblem(i.IndexSignatures.Count, Members(i)),
        AliasDeclaration { Type: ObjectType o } => RecordProblem(o.IndexSignatures.Count, o.Members.OfType<PropertyMember>()),
        AliasDeclaration a => Values(a.Type) switch
        {
            [] => new("only `null` and `undefined`"),
            [var single] => MapProblem(single),
            _ => UnionProblem(a.Type, a.Name),
        },
        EnumDeclaration e => e.Members.Select(m => m.Value.ValueKind).Distinct().Count() > 1 ? new("an enum of strings and numbers")
            : NonInteger(e.Members.Select(m => m.Value)),
        ClassDeclaration c => IsClosed(c) && DiscriminantOrNull(c) is null ? new($"the closed base `{c.Name}`, whose subclasses have no string discriminant (R19)") : null,
        _ => null,
    };

    /// <summary>What <see cref="Map"/> refuses, without the declarations the type names.</summary>
    private Unmappable? MapProblem(IrType type) => type switch
    {
        PrimitiveType { Name: "number" or "string" or "boolean" } => null,
        RefType { Module: not null } r when Find(r) is { } d =>
            d is ClassDeclaration c && !IsValueLike(c) ? new($"the entity `{c.Name}` in a data position (R17)") : null,
        ArrayType a => MapProblem(a.Element),
        TupleType { Required: null, Rest: false, Elements.Count: > 0 } t => t.Elements.Select(MapProblem).FirstOrDefault(p => p is not null)
            ?? (NameOf(t) is null ? new("a tuple of different types") : null), // R5
        UnionType u => Values(u).Count < 2 ? new("a nullable array or tuple element") : UnionProblem(u, null),
        PrimitiveType p => new($"`{p.Name}` here"),
        LiteralType => new("a literal here"),
        ThisType => new("`this` outside a live member"),
        RefType r => new($"`{r.Name}` here"),
        ObjectType => new("an inline object type"),
        TupleType => new("a tuple with optional or rest elements"),
        IntersectionType => new("an intersection"),
        _ => new($"the IR type {type.GetType().Name}"),
    };

    /// <summary>
    /// What <see cref="Union"/> and the union reader (R19) refuse: a literal next to other types, a value-type case (R2
    /// keeps unions non-boxing), a non-integer number, a discriminant that isn't a string on every case, or cases it
    /// can't tell apart, which is IOP2003.
    /// </summary>
    private Unmappable? UnionProblem(IrType type, string? name)
    {
        var flat = Values(type);
        var label = name is null ? "a union" : $"the union `{name}`";
        var ambiguous = name is null ? "an ambiguous union" : $"the ambiguous union `{name}`";
        if (flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.String } || IsOpenString(t)))
        {
            return null; // R9, R10
        }
        if (flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.Number }))
        {
            return NonInteger(flat.Select(t => ((LiteralType)t).Value));
        }
        if (flat.Any(t => t is LiteralType))
        {
            return new($"{label} of a literal and other types");
        }
        var cases = new List<(string? Name, JsonKind Json, Declaration? Declaration)>();
        foreach (var t in flat)
        {
            if (MapProblem(t) is { } problem)
            {
                return problem;
            }
            if (ValueType(t) is { } valueType)
            {
                return new($"{label} with the value-type case {valueType} (R2)");
            }
            if (NameOf(t) is not { } caseName || cases.All(c => c.Name != caseName)) // R5
            {
                cases.Add((NameOf(t), JsonOf(t), t is RefType r ? Find(r) : null));
            }
        }
        if (type is UnionType u && u.Types.Count(t => !IsNullish(t)) == flat.Count && u.Discriminant is { } discriminant) // as Union() reads it
        {
            return cases.All(c => c.Declaration is { } d && StringConstants(d).Contains(discriminant)) ? null
                : new($"{label}, whose discriminant `{discriminant}` isn't a string on every case (R19)");
        }
        var objects = cases.Where(c => c.Json == JsonKind.Object).ToList();
        if (cases.Except(objects).GroupBy(c => c.Json).FirstOrDefault(g => g.Count() > 1) is { } same)
        {
            return new($"{ambiguous}: {string.Join(" and ", same.Select(c => c.Name ?? "an anonymous type"))} are both JSON {same.Key} (R19)",
                Diagnostic.Ambiguous);
        }
        foreach (var c in objects.Count > 1 ? objects : [])
        {
            var others = objects.Where(o => o != c).Select(o => WireNames(o.Declaration)?.All).ToList();
            if (WireNames(c.Declaration) is not { } own || others.Any(o => o is null) ||
                !own.Required.Any(m => others.All(o => !o!.Contains(m))))
            {
                return new($"{ambiguous}: {c.Name} has no required member the other cases lack (R19)", Diagnostic.Ambiguous);
            }
        }
        return null;
    }

    private static Unmappable? NonInteger(IEnumerable<JsonElement> values) =>
        values.FirstOrDefault(v => v.ValueKind == JsonValueKind.Number && !v.TryGetInt32(out _)) is { ValueKind: JsonValueKind.Number } value
            ? new($"the non-integer enum value {value}")
            : null;

    private Unmappable? RecordProblem(int indexSignatures, IEnumerable<PropertyMember> members) =>
        indexSignatures > 0 ? new("an index signature")
        : members.Where(p => !p.Static && p.Type is not LiteralType).Select(p => Problem(p.Type, live: false)).FirstOrDefault(p => p is not null);

    /// <summary>The string constants of a record, or a class's own (R20), in order.</summary>
    private List<string> StringConstants(Declaration declaration) =>
        [.. RecordMembers(declaration).Where(p => p.Type is LiteralType { Value.ValueKind: JsonValueKind.String }).Select(p => p.Name)];

    /// <summary>
    /// R19 step 3 without mapping: an object case's required wire members (no more than its record has) and the names
    /// of all its members and its bases' (no fewer); null when it isn't a record.
    /// </summary>
    private (List<string> Required, HashSet<string> All)? WireNames(Declaration? declaration)
    {
        if (declaration is not (InterfaceDeclaration or AliasDeclaration { Type: ObjectType } or ClassDeclaration))
        {
            return null;
        }
        var members = RecordMembers(declaration).ToList();
        var required = members.Where(p => p.Type is LiteralType || declaration is not ClassDeclaration && !p.Optional).Select(p => p.Name).ToList();
        if (declaration is ClassDeclaration closed && IsClosed(closed) && DiscriminantOrNull(closed) is { } discriminant)
        {
            required.Insert(0, discriminant);
        }
        var all = declaration is ClassDeclaration c ? Ancestors(c).SelectMany(RecordMembers) : members;
        return (required, [.. all.Select(p => p.Name)]);
    }

    private IEnumerable<PropertyMember> RecordMembers(Declaration declaration) => (declaration switch
    {
        InterfaceDeclaration i => Members(i),
        AliasDeclaration { Type: ObjectType o } => o.Members.OfType<PropertyMember>(),
        ClassDeclaration c => c.Members.OfType<PropertyMember>(),
        _ => [],
    }).Where(p => !p.Static);

    /// <summary>A class and the classes it extends.</summary>
    private IEnumerable<ClassDeclaration> Ancestors(ClassDeclaration? c)
    {
        for (; c is not null; c = c.Extends is RefType r ? Find(r) as ClassDeclaration : null)
        {
            yield return c;
        }
    }

    /// <summary>The C# type <see cref="Map"/> gives, if it's a value type (R2).</summary>
    private string? ValueType(IrType type) => type switch
    {
        PrimitiveType { Name: "number" } => "double",
        PrimitiveType { Name: "boolean" } => "bool",
        RefType { Module: not null } r => Find(r) switch
        {
            EnumDeclaration e => Naming.Type(e.Name),
            AliasDeclaration { Type: not ObjectType } a => Values(a.Type) is [var single] ? ValueType(single) : Naming.Type(a.Name),
            _ => null,
        },
        _ => null,
    };

    /// <summary>The name of the C# type <see cref="Map"/> gives, or null for an anonymous one.</summary>
    private string? NameOf(IrType type) => type switch
    {
        PrimitiveType { Name: "number" } => "double",
        PrimitiveType { Name: "string" } => "string",
        PrimitiveType { Name: "boolean" } => "bool",
        RefType { Module: not null } r => Find(r) switch
        {
            ClassDeclaration c => IsValueLike(c) ? SnapshotName(c) : Naming.Type(c.Name),
            AliasDeclaration { Type: not ObjectType } a when Values(a.Type) is [var single] => NameOf(single),
            { } d => Naming.Type(d.Name),
            null => null,
        },
        ArrayType a => NameOf(a.Element) is { } element ? element + "[]" : null,
        TupleType { Required: null, Rest: false, Elements.Count: > 0 } t =>
            t.Elements.Select(NameOf).Distinct().ToList() is [{ } element] ? element + "[]" : null,
        _ => null,
    };

    /// <summary>The JSON a union case starts with (R19 step 4), once value types are ruled out.</summary>
    private JsonKind JsonOf(IrType type) => type switch
    {
        PrimitiveType => JsonKind.String,
        ArrayType or TupleType => JsonKind.Array,
        RefType { Module: not null } r when Find(r) is AliasDeclaration { Type: not ObjectType } a &&
            Values(a.Type) is [var single] => JsonOf(single),
        _ => JsonKind.Object,
    };

    private Declaration? Find(RefType type) => type.Module is { } module ? declarations.GetValueOrDefault((module, type.Name)) : null;

    /// <summary>What selection skips can't reach the mapping.</summary>
    private static UnreachableException Unreachable(string subject) => new($"{subject}: selection should have skipped this.");
}
