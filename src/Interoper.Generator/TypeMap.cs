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
    public string TypeName => Opt ? $"global::Interoper.Opt<{Type!.Name}>" : Type!.Name;
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

/// <summary>
/// A generated union struct (R2). <see cref="Discriminant"/> is the property its object cases are told apart by (R19
/// step 2). A <see cref="WrittenOnly"/> one is a parameter's that has an entity case or had a case outside the selection:
/// it's never read, so R19 doesn't apply.
/// </summary>
internal sealed record UnionStruct(TypeName Type, IReadOnlyList<UnionCase> Cases, string? Discriminant, bool WrittenOnly = false) : DataType(Type);

/// <param name="Declaration">What an object case is read as (R19 steps 2 and 3).</param>
internal sealed record UnionCase(CsType Type, Declaration? Declaration);

/// <summary>Where a type is, which decides what it can be besides data: an entity (R17), <c>this</c> (§5), a callback (R21).</summary>
internal enum Position
{
    /// <summary>A data record's member, an array's element, a callback's argument or result: an entity too (R17).</summary>
    Data,

    /// <summary>A live member's type or result, or an event's payload: also <c>this</c>.</summary>
    Member,

    /// <summary>A parameter, or a property's setter: also a callback (R21), and a union that is only written (R2).</summary>
    Parameter,
}

/// <summary>
/// How the selected declarations map to C#: the type of each IR type, the fields of each record, and the enums and unions
/// that unions become, including the anonymous ones named after their owner (R27), which it collects in <see cref="Owned"/>.
/// </summary>
internal sealed class TypeMap
{
    private const int MaxEnumMembers = 64; // R10
    private const int MaxCallbackParameters = 4; // R21

    private readonly Dictionary<(string Module, string Name), Declaration> declarations = [];
    private readonly Dictionary<Declaration, string> moduleOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ClassDeclaration, string> ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IrModule> modules = [];
    private readonly string package, root;
    private readonly List<ClassDeclaration> classes = [];
    private readonly HashSet<string> valueClasses;
    private readonly ICollection<Diagnostic> diagnostics;
    private readonly Dictionary<TypeName, DataType?> unions = [];
    private readonly Dictionary<Declaration, IReadOnlyList<Field>> fields = new(ReferenceEqualityComparer.Instance);
    private readonly List<DataType> owned = [];
    private readonly HashSet<ClassDeclaration> watchable = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Declaration, bool> scoped = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Declaration> scoping = new(ReferenceEqualityComparer.Instance);
    /// <summary>R27: the names of declarations a sibling module's type of the same name has taken (<c>BOptions</c>).</summary>
    private readonly Dictionary<(string Module, string Name), string> renamed = [];
    /// <summary>R25: each distinct enum member rename, the enum it was first met in, and how many others have it.</summary>
    private readonly OrderedDictionary<string, (string Subject, int Others)> enumRenames = [];

    /// <summary>
    /// <paramref name="selected"/> stand for themselves (a class trimmed to what can be mapped); its classes are the
    /// live classes to emit. <paramref name="ns"/> is the root namespace (R27).
    /// </summary>
    public TypeMap(IrDocument ir, Profile profile, IEnumerable<Selected> selected, ICollection<Diagnostic> diagnostics, string ns)
    {
        foreach (var module in ir.Modules)
        {
            modules[module.Specifier] = module;
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
        var emitted = selected.Select(s => (s.Module, s.Declaration.Name)).ToHashSet();
        // R27: sibling modules' types of one name. The first in IR order keeps it; a later one is prefixed with its
        // module's class name (ops/b.js's Options is BOptions), so both compile, and so do their users. The records of
        // inline event details (R28), which the IR doesn't declare, come last.
        var owners = new Dictionary<(string Namespace, string Name), string>();
        var declared = ir.Modules.SelectMany(m => m.Declarations.Select(d => (Module: m.Specifier, Declaration: d))).ToList();
        var inIr = declared.Select(x => (x.Module, x.Declaration.Name)).ToHashSet();
        foreach (var (module, d) in declared.Concat(selected.Where(s => !inIr.Contains((s.Module, s.Declaration.Name))).Select(s => (s.Module, s.Declaration))))
        {
            if (!MakesType(d))
            {
                continue;
            }
            var key = (Namespace: Namespace(module), Name: Naming.Type(d.Name));
            if (owners.TryAdd(key, module) || owners[key] == module)
            {
                continue;
            }
            var name = LiveEmitter.ModuleClass(module) + key.Name;
            renamed[(module, d.Name)] = name;
            if (emitted.Contains((module, d.Name)))
            {
                diagnostics.Add(new(Diagnostic.Renamed, module, $"its {key.Item2} is {name}: {owners[key]} has one too (R27)"));
            }
        }
        // R27: the namespaces of the emitted types, below the root and with the namespaces they're in. The registry
        // takes the next free name when one of them has its name (Widgets2 next to a widgets/ folder).
        Namespaces = selected.Select(s => Namespace(s.Module)).SelectMany(Parents).ToHashSet();
        var registry = Naming.Type(ir.Package.Name.Split('/')[^1]);
        Registry = registry;
        for (var n = 2; Namespaces.Contains($"{root}.{Registry}"); n++)
        {
            Registry = $"{registry}{n}";
        }
        if (Registry != registry)
        {
            diagnostics.Add(new(Diagnostic.Renamed, $"{root}.{registry}", $"the registry is {Registry}: a namespace has its name (R27)"));
        }
        // R27: classes of one name in different namespaces need different class id constants. Each one outside the root
        // namespace gets its namespace below the root as a prefix (LayersPointId), whatever the IR order.
        foreach (var group in classes.GroupBy(c => Name(c).Name))
        {
            foreach (var c in group)
            {
                var type = Name(c);
                var id = group.Count() == 1 || type.Namespace == root ? type.Name : type.Namespace[(root.Length + 1)..].Replace(".", "") + type.Name;
                if (id != type.Name)
                {
                    diagnostics.Add(new(Diagnostic.Renamed, type.ToString(), $"its class id is {id}Id: other classes are named {type.Name} (R27)"));
                }
                ids[c] = id + "Id";
            }
        }
    }

    /// <summary>
    /// The generated registry class, named after the package without its scope (<c>DemoMaps</c>), in the root namespace;
    /// with a numeric suffix when a namespace has that name (R27).
    /// </summary>
    public string Registry { get; }

    /// <summary>The namespaces of the emitted types below the root, and the namespaces they're in (R27).</summary>
    public IReadOnlySet<string> Namespaces { get; }

    /// <summary>A namespace and the ones it's in, below the root: Root.A.B gives Root.A.B and Root.A.</summary>
    private IEnumerable<string> Parents(string ns)
    {
        for (; ns.Length > root.Length; ns = ns[..ns.LastIndexOf('.')])
        {
            yield return ns;
        }
    }

    /// <summary>Whether a declaration becomes a C# type of its name: a record, a live class, an enum or a union.</summary>
    private bool MakesType(Declaration d) => d switch
    {
        InterfaceDeclaration or EnumDeclaration or ClassDeclaration or AliasDeclaration { Type: ObjectType } => true,
        AliasDeclaration { Type: not FunctionType } a => Values(a.Type).Count > 1,
        _ => false,
    };

    /// <summary>R27: the namespace of a declaration's types: the root namespace plus its module's folders.</summary>
    public string Namespace(Declaration declaration) => Namespace(moduleOf[declaration]);

    /// <summary>R27: the namespace of a module's types: the root namespace, another package's name, and its folders.</summary>
    public string Namespace(string module) => modules.GetValueOrDefault(module)?.Package is { } other
        ? Naming.Namespace(root, other, module, otherPackage: true)
        : Naming.Namespace(root, package, module);

    /// <summary>The package a module is in.</summary>
    public string PackageOf(string module) => modules.GetValueOrDefault(module)?.Package ?? package;

    /// <summary>Whether no export of its package reaches a module, which then can't be imported (ir.md).</summary>
    public bool IsInternal(string module) => modules.GetValueOrDefault(module)?.Internal ?? false;

    /// <summary>The type a declaration becomes, in its namespace (R27).</summary>
    public TypeName Name(Declaration declaration) =>
        new(Namespace(declaration), renamed.GetValueOrDefault((moduleOf[declaration], declaration.Name)) ?? Naming.Type(declaration.Name));

    /// <summary>A class's snapshot record (R18), in the class's namespace.</summary>
    public TypeName Snapshot(ClassDeclaration c) => new(Namespace(c), SnapshotName(c));

    /// <summary>The constant holding a live class's class id (design §2), as code in namespace <paramref name="ns"/> names it.</summary>
    public string ClassId(ClassDeclaration c, string ns) => $"{new TypeName(root, Registry).In(ns)}.{ClassIdName(c)}";

    /// <summary>The name of that constant in the registry: <c>PointId</c>.</summary>
    public string ClassIdName(ClassDeclaration c) => ids[c];

    /// <summary>Anonymous enums and unions named after their owner (R27), in the order they were met.</summary>
    public IReadOnlyList<DataType> Owned => owned;

    public string SnapshotName(ClassDeclaration c) => Name(c).Name + "Snapshot";

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

    /// <summary>The live class a class derives from, with its type arguments, as code in namespace <paramref name="ns"/> names it.</summary>
    public string LiveBaseName(ClassDeclaration c, string ns) =>
        LiveBase(c) is { } b ? CsType.Live(Name(b), [.. ((RefType)c.Extends!).Args.Select(a => Map(a, Name(c)))]).In(ns).Name : "global::Interoper.JsObject";

    /// <summary>A live class as its own members name it, for <c>this</c>: with its type parameters (mapping §2).</summary>
    public CsType Self(ClassDeclaration c) => CsType.Live(Name(c), [.. c.TypeParameters.Select(t => CsType.TypeParameter(t.Name))]);

    /// <summary>
    /// Whether a class gets <c>Create</c> (R18): it isn't abstract, its module isn't internal, and its constructor takes
    /// nothing or a bag (an object type or a record), whose members its snapshot carries.
    /// </summary>
    public bool Constructible(ClassDeclaration c) =>
        !c.Abstract && !IsInternal(moduleOf[c]) && (c.Constructors.Count == 0 || c.Constructors.Any(s => s.Params.Count == 0 || IsBag(s.Params[0].Type)));

    private bool IsBag(IrType type) => Values(type) is [var bag] &&
        (bag is ObjectType || bag is RefType r && Find(r) is InterfaceDeclaration or AliasDeclaration { Type: ObjectType });

    /// <summary>R21: a property typed with a callback, which is setter-only.</summary>
    public bool IsCallback(PropertyMember p) => CallbackOf(p.Type) is not null;

    /// <summary>R13: a property typed with a DOM element (or null), which is setter-only: JS can't hand an element back.</summary>
    public bool IsElement(PropertyMember p) => ElementOf(p.Type);

    /// <summary>
    /// R13: a DOM element type, which is an <c>ElementReference</c>: <c>Element</c>, <c>HTMLElement</c>,
    /// <c>SVGElement</c> and their <c>HTML…Element</c> and <c>SVG…Element</c> subtypes.
    /// </summary>
    internal static bool IsElement(IrType type) =>
        type is RefType { Module: null, Name: var name } &&
        (name is "Element" or "HTMLElement" or "SVGElement" || (name.StartsWith("HTML", StringComparison.Ordinal) || name.StartsWith("SVG", StringComparison.Ordinal)) && name.EndsWith("Element", StringComparison.Ordinal));

    /// <summary>A DOM element type, or one with null and undefined: a member that is one is written only (R13).</summary>
    private static bool ElementOf(IrType type) =>
        IsElement(type) || type is UnionType u && u.Types.Count(t => !IsNullish(t)) == 1 && u.Types.Any(IsElement);

    /// <summary>
    /// R13: a data record with a member that is a DOM element: it can be written (<c>$e</c>), but not read, so it has
    /// no <c>Read</c> and is only a parameter's.
    /// </summary>
    public bool HoldsElement(Declaration declaration) =>
        declaration is (InterfaceDeclaration or AliasDeclaration { Type: ObjectType }) && RecordMembers(declaration).Any(p => ElementOf(p.Type));

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
        PrimitiveType { Name: "any" or "unknown" or "object" } => CsType.Any,
        RefType { Module: not null } r when Find(r) is { } declaration => Named(declaration, r.Args, owner),
        RefType r when IsElement(r) => CsType.DomElement, // R13
        TypeParameterType t => CsType.TypeParameter(t.Name),
        FunctionType f => Callback(f, owner),
        ArrayType a => CsType.Array(Map(a.Element, owner)),
        TupleType { Required: null, Rest: false, Elements.Count: > 0 } t
            when t.Elements.Select(e => Map(e, owner).FullName).Distinct().Count() == 1 => CsType.Array(Map(t.Elements[0], owner)), // R5
        UnionType u when Union(owner, u) is { } union => Own(union),
        _ => throw Unreachable(owner.Name),
    };

    /// <summary>
    /// The type of a member or parameter: without null and undefined, which make it nullable. A nullable type is
    /// <c>Opt&lt;T&gt;</c> (R3), unless it's a union, which has those states itself (R2). A member of a live class
    /// (<paramref name="self"/>, R16) can also be <c>this</c>.
    /// </summary>
    public (CsType Type, bool Nullable) Slot(IrType type, TypeName owner, CsType? self = null)
    {
        var nullable = false;
        if (type is UnionType u && u.Types.Any(IsNullish))
        {
            var rest = u.Types.Where(t => !IsNullish(t)).ToList();
            type = rest.Count == 1 ? rest[0] : u with { Types = rest };
            nullable = true;
        }
        var mapped = type is ThisType && self is not null ? self : Map(type, owner);
        return (mapped, nullable || Flatten(type).Any(t => IsNullish(t) || t is PrimitiveType { Name: "any" or "unknown" }));
    }

    /// <summary>
    /// R21: a callback, an <c>Action</c> or a <c>Func</c>; an anonymous type in it is named after its owner and the
    /// parameter (R27).
    /// </summary>
    private CsType Callback(FunctionType f, TypeName owner)
    {
        var parameters = new List<(CsType, bool)>();
        foreach (var p in f.Params)
        {
            var (type, nullable) = Slot(p.Type, owner.Owned(p.Name));
            parameters.Add((type, (nullable || p.Optional) && !type.IsUnion));
        }
        (CsType, bool)? result = null;
        if (f.Returns is not PrimitiveType { Name: "void" or "undefined" })
        {
            var (type, nullable) = Slot(f.Returns, owner);
            result = (type, nullable && !type.IsUnion);
        }
        return CsType.Callback(parameters, result);
    }

    /// <summary>
    /// R28: whether a value renders as an HTML attribute, in prerendering and static SSR too: a string, number or boolean,
    /// or a string enum (R9), the types with a fast path.
    /// </summary>
    public bool IsAttribute(IrType type, TypeName owner) => Slot(type, owner).Type.Fast is not null;

    /// <summary>Whether a result mentions a value-like class, so it's read by value (R16, design §3.4 <c>byValue</c>).</summary>
    public bool ByValue(IrType type) => Mentions(type, IsValueLike);

    private CsType Named(Declaration declaration, IReadOnlyList<IrType> args, TypeName owner) => declaration switch
    {
        InterfaceDeclaration or AliasDeclaration { Type: ObjectType } =>
            CsType.Record(Name(declaration), [.. args.Select(a => Map(a, owner))], IsScoped(declaration)), // R17
        ClassDeclaration c when IsValueLike(c) => CsType.Record(Snapshot(c)), // R16
        ClassDeclaration c => CsType.Live(Name(c), [.. args.Select(a => Map(a, owner))]),
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
        var all = Values(type);
        // R2: a parameter's union drops its cases outside the selection, and is then only written.
        var flat = all.Where(t => !IsUnselected(t)).ToList();
        foreach (var dropped in all.Except(flat).OfType<RefType>())
        {
            diagnostics.Add(new(Diagnostic.NotSupported, name.Name, $"case `{dropped.Name}` dropped: a class outside the selection, so it's only written (R2)"));
        }
        var literals = flat.OfType<LiteralType>().Select(l => l.Value).ToList();
        DataType? result =
            flat.Count < 2 ? null
            : flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.String } || IsOpenString(t)) ? new EnumType(name,
                EnumMembers(name.Name, literals, literals.Select(v => v.GetString()!)), literals.Count > MaxEnumMembers || flat.Any(IsOpenString))
            : flat.All(t => t is LiteralType { Value.ValueKind: JsonValueKind.Number }) ? new EnumType(name,
                EnumMembers(name.Name, literals, literals.Select(v => v.GetRawText())), Extensible: false)
            : new UnionStruct(name, Cases(name, flat),
                type is UnionType u && u.Types.Count(t => !IsNullish(t)) == all.Count ? u.Discriminant : null,
                WrittenOnly(all, flat));
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

    /// <summary>
    /// R25: names for the values. A rename is reported once, for the first enum it's met in: an anonymous union that
    /// many types repeat makes an enum for each.
    /// </summary>
    private List<(string, JsonElement)> EnumMembers(string type, List<JsonElement> values, IEnumerable<string> names)
    {
        var renames = new List<Diagnostic>();
        var members = Naming.EnumMembers(names, type, renames);
        foreach (var rename in renames)
        {
            enumRenames[rename.Message] = enumRenames.TryGetValue(rename.Message, out var known) ? known with { Others = known.Others + 1 } : (type, 0);
        }
        return [.. members.Zip(values)];
    }

    /// <summary>R25: reports each enum member rename once, with how many other enums have it too.</summary>
    public void ReportEnumRenames()
    {
        foreach (var (message, (subject, others)) in enumRenames)
        {
            diagnostics.Add(new(Diagnostic.Renamed, subject, others switch
            {
                0 => message,
                1 => $"{message}, and in 1 other enum",
                _ => $"{message}, and in {others} other enums",
            }));
        }
    }

    /// <summary>The cases of a union, after mapping (R5: one per C# type).</summary>
    private List<UnionCase> Cases(TypeName name, List<IrType> types)
    {
        var cases = new List<UnionCase>();
        foreach (var type in types)
        {
            var mapped = Map(type, name);
            if (cases.All(c => c.Type.FullName != mapped.FullName))
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

    /// <summary>
    /// R2: a union that dropped a case outside the selection, or has an entity case, which needs a scope to be read: only
    /// a parameter's, never read.
    /// </summary>
    private bool WrittenOnly(List<IrType> all, List<IrType> flat) => flat.Count < all.Count || flat.Any(UsesEntity);

    /// <summary>The first type parameter a type names, if any.</summary>
    private static TypeParameterType? TypeParameterIn(IrType type) =>
        type as TypeParameterType ?? Selection.Children(type).Select(TypeParameterIn).FirstOrDefault(t => t is not null);

    /// <summary>A class outside the selection: a parameter's union drops it as a case (R2).</summary>
    internal bool IsUnselected(IrType type) => type is RefType { Module: not null } r && Find(r) is ClassDeclaration c && !classes.Contains(c);

    /// <summary>Whether a type has JS null in it, which a missing callback is sent as; otherwise it's undefined (R21).</summary>
    public bool HasNull(IrType type) => Flatten(type).Any(t => t is PrimitiveType { Name: "null" });

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

    /// <summary>
    /// R24: an event payload's default projection, the wire members of its data record, constants included; null for
    /// any other payload, which is sent whole.
    /// </summary>
    public IReadOnlyList<string>? Projection(IrType payload) =>
        payload is RefType r && Find(r) is (InterfaceDeclaration { IndexSignatures.Count: 0 } or AliasDeclaration { Type: ObjectType { IndexSignatures.Count: 0 } }) and var d
            ? [.. Fields(d).Select(f => f.JsName)]
            : null;

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
        var taken = new HashSet<string>(result.Select(f => f.Name)) { SnapshotName(c), "Write", "WriteMembers", "Read", "Diff" };
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

    /// <summary>
    /// R18: a writable data member is in the snapshot; R17: one that mentions an entity isn't, nor is a callback (R21) or
    /// a DOM element (R13), which can't be read.
    /// </summary>
    private bool IsSnapshotMember(PropertyMember p) =>
        !p.Static && p.Type is not LiteralType && !p.Readonly && !UsesEntity(p.Type) && CallbackOf(p.Type) is null && !ElementOf(p.Type);

    /// <summary>
    /// Whether a type needs a scope to be read (R17): it mentions an entity class, a type parameter (an entity, mapping
    /// §2), <c>this</c>, <c>any</c> (a <c>JsAny</c>), or a record that does.
    /// </summary>
    private bool UsesEntity(IrType type) => type switch
    {
        TypeParameterType or ThisType => true,
        PrimitiveType { Name: "any" or "unknown" or "object" } => true, // a JsAny, whose handles the scope owns
        RefType { Module: not null } r => Find(r) switch
        {
            ClassDeclaration c => !IsValueLike(c),
            AliasDeclaration { Type: not ObjectType } a => UsesEntity(a.Type),
            { } d => IsScoped(d),
            null => false,
        },
        UnionType u => u.Types.Any(UsesEntity),
        ArrayType a => UsesEntity(a.Element),
        TupleType t => t.Elements.Any(UsesEntity),
        _ => false,
    };

    /// <summary>
    /// R17: whether a data record holds an entity, so it's read with a scope: <c>Read(ref reader, scope)</c>, and what
    /// returns it takes the scope. A record that holds itself is scoped when something else in it is.
    /// </summary>
    public bool IsScoped(Declaration declaration)
    {
        if (declaration is not (InterfaceDeclaration or AliasDeclaration { Type: ObjectType }))
        {
            return false;
        }
        if (scoped.TryGetValue(declaration, out var known))
        {
            return known;
        }
        if (!scoping.Add(declaration))
        {
            return false;
        }
        var result = RecordMembers(declaration).Any(p => p.Type is not LiteralType && UsesEntity(p.Type));
        scoping.Remove(declaration);
        if (result || scoping.Count == 0) // "not scoped" inside a recursion may only mean the record further up isn't known yet
        {
            scoped[declaration] = result;
        }
        return result;
    }

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
    /// Why a type can't be mapped where it is (<paramref name="position"/>), or null. Anywhere an entity is a live object;
    /// in a data record that makes the record scoped (R17). <c>this</c> is one in a live member, and a callback is only
    /// a parameter's (R21). It checks what <see cref="Slot"/> would refuse, but not the declarations the type names:
    /// selection checks each of those itself (<see cref="Problem(Declaration)"/>).
    /// </summary>
    public Unmappable? Problem(IrType type, Position position)
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
        return CallbackOf(type) is { } callback ? (position == Position.Parameter ? CallbackProblem(callback) : new("a callback that isn't a parameter (R21)"))
            : type is ThisType && position != Position.Data ? null
            : MapProblem(type, written: position == Position.Parameter);
    }

    /// <summary>A result's, without its Promise (R8); <c>void</c> is none.</summary>
    public Unmappable? ResultProblem(IrType returns) => returns switch
    {
        RefType { Module: null, Name: "Promise", Args: [var inner] } => ResultProblem(inner),
        PrimitiveType { Name: "void" or "undefined" } => null,
        _ => Problem(returns, Position.Member),
    };

    /// <summary>
    /// A live class's property: its getter's and setter's types, and its snapshot member's (R18). A constant is fine
    /// (R20), and so is a callback or a DOM element that can be set, which is setter-only (R21, R13).
    /// </summary>
    public Unmappable? Problem(PropertyMember p) =>
        p.Type is LiteralType ? null
        : IsCallback(p) ? (p.Readonly ? new("a callback that can't be set (R21)") : Problem(p.WriteType ?? p.Type, Position.Parameter))
        : IsElement(p) ? (p.Readonly ? new("a DOM element JS returns (R13)") : Problem(p.WriteType ?? p.Type, Position.Parameter))
        : Problem(p.Type, Position.Member)
            ?? (p.WriteType is { } w ? Problem(w, CallbackOf(w) is null ? Position.Parameter : Position.Member) : null) // a setter's is written
            ?? (IsSnapshotMember(p) ? Problem(p.Type, Position.Data) : null);

    /// <summary>
    /// A declaration's own problem, or null: a data record's members (R15), an enum's values, what a union alias becomes
    /// (R2, R9, R19), a callback alias's callback (R21), a closed base's discriminant (R19 step 2), and a generic type's
    /// type parameters, which are entities (mapping §2).
    /// </summary>
    public Unmappable? Problem(Declaration declaration) => declaration switch
    {
        InterfaceDeclaration i => TypeParametersProblem(i.TypeParameters) ?? RecordProblem(i.IndexSignatures.Count, Members(i)),
        AliasDeclaration { Type: ObjectType o } => RecordProblem(o.IndexSignatures.Count, o.Members.OfType<PropertyMember>()),
        AliasDeclaration { Type: FunctionType f } => CallbackProblem(f),
        AliasDeclaration a => Values(a.Type) switch
        {
            [] => new("only `null` and `undefined`"),
            [var single] => MapProblem(single),
            _ => UnionProblem(a.Type, a.Name, written: true), // where it's read, its use is checked again (MapProblem)
        },
        EnumDeclaration e => e.Members.Select(m => m.Value.ValueKind).Distinct().Count() > 1 ? new("an enum of strings and numbers")
            : NonInteger(e.Members.Select(m => m.Value)),
        ClassDeclaration c => IsClosed(c) && DiscriminantOrNull(c) is null ? new($"the closed base `{c.Name}`, whose subclasses have no string discriminant (R19)")
            : TypeParametersProblem(c.TypeParameters),
        _ => null,
    };

    /// <summary>
    /// A generic type's type parameters are entities (<c>where T : JsObject</c>), so they take no constraint (mapping §2).
    /// A default doesn't matter: C# always has the type argument.
    /// </summary>
    private static Unmappable? TypeParametersProblem(IEnumerable<TypeParameter> parameters) =>
        parameters.FirstOrDefault(t => t.Constraint is not null) is { } t ? new($"the type parameter `{t.Name}` with a constraint (§2)") : null;

    /// <summary>
    /// R21: a callback's arguments are read and its result written as data, with up to
    /// <see cref="MaxCallbackParameters"/> parameters and no type parameters or rest parameter.
    /// </summary>
    private Unmappable? CallbackProblem(FunctionType f) =>
        f.TypeParameters.Count > 0 ? new("a generic callback (R21)")
        : f.Params.Count > MaxCallbackParameters ? new($"a callback with more than {MaxCallbackParameters} parameters (R21)")
        : f.Params.Any(p => p.Rest) ? new("a callback with a rest parameter (R21)")
        : f.Params.Select(p => Problem(p.Type, Position.Data)).FirstOrDefault(p => p is not null)
            ?? f.Returns switch
            {
                PrimitiveType { Name: "void" or "undefined" } => null,
                RefType { Module: null, Name: "Promise" } => new("a callback that returns a Promise (R21)"),
                var returns => Problem(returns, Position.Data),
            };

    /// <summary>The callback a type is (R21): a function type, or a named alias of one; null for anything else.</summary>
    private FunctionType? CallbackOf(IrType type) => (Values(type) is [var single] ? single : type) switch
    {
        FunctionType f => f,
        RefType { Module: not null } r when Find(r) is AliasDeclaration { TypeParameters.Count: 0, Type: FunctionType f } => f,
        _ => null,
    };

    /// <summary>
    /// What <see cref="Map"/> refuses, without the declarations the type names; a union alias's use is checked where it's
    /// read, since a parameter's may be only written (<paramref name="written"/>, R2).
    /// </summary>
    private Unmappable? MapProblem(IrType type, bool written = false) => type switch
    {
        PrimitiveType { Name: "number" or "string" or "boolean" or "any" or "unknown" or "object" } => null,
        RefType { Module: not null } r when Find(r) is { } d => d switch
        {
            ClassDeclaration c when !IsValueLike(c) => ArgumentsProblem(c.Name, c.TypeParameters, r.Args),
            InterfaceDeclaration or AliasDeclaration { Type: ObjectType } when !written && HoldsElement(d) =>
                new($"the record `{d.Name}`, which holds a DOM element JS can't return (R13)"),
            InterfaceDeclaration i => ArgumentsProblem(i.Name, i.TypeParameters, r.Args),
            AliasDeclaration { Type: FunctionType } => new("a callback that isn't a parameter (R21)"),
            AliasDeclaration { Type: not ObjectType } a when !written && Values(a.Type).Count > 1 => UnionProblem(a.Type, a.Name, written: false),
            _ => null,
        },
        TypeParameterType => null, // an entity (mapping §2); selection checks it's the type's own
        RefType r when IsElement(r) => written ? null : new("a DOM element JS returns (R13)"),
        ArrayType a => MapProblem(a.Element, written),
        TupleType { Required: null, Rest: false, Elements.Count: > 0 } t => t.Elements.Select(e => MapProblem(e, written)).FirstOrDefault(p => p is not null)
            ?? (NameOf(t, full: true) is null ? new("a tuple of different types") : null), // R5
        UnionType u => Values(u).Count < 2 ? new("a nullable array or tuple element") : UnionProblem(u, null, written),
        FunctionType => new("a callback that isn't a parameter (R21)"),
        PrimitiveType p => new($"`{p.Name}` here"),
        LiteralType => new("a literal here"),
        ThisType => new("`this` outside a live member"),
        RefType r => new($"`{r.Name}` here"),
        ObjectType => new("an inline object type"),
        TupleType => new("a tuple with optional or rest elements"),
        IntersectionType => new("an intersection"),
        _ => new($"the IR type {type.GetType().Name}"),
    };

    /// <summary>A generic type's type arguments are entities (mapping §2): <c>Collection&lt;Marker&gt;</c>, not <c>Collection&lt;number&gt;</c>.</summary>
    private Unmappable? ArgumentsProblem(string name, IReadOnlyList<TypeParameter> parameters, IReadOnlyList<IrType> args) =>
        args.Count != parameters.Count ? new($"`{name}` with {args.Count} type arguments for {parameters.Count} type parameters")
        : args.Any(a => !IsEntity(a)) ? new($"`{name}` of a type that isn't an entity (§2)")
        : args.Select(a => MapProblem(a)).FirstOrDefault(p => p is not null);

    /// <summary>A live object as data: an entity class, or a type parameter, which is one (mapping §2).</summary>
    private bool IsEntity(IrType type) =>
        type is TypeParameterType || type is RefType { Module: not null } r && Find(r) is ClassDeclaration c && !IsValueLike(c);

    /// <summary>
    /// What <see cref="Union"/> and the union reader (R19) refuse: a literal next to other types, a value-type case (R2
    /// keeps unions non-boxing), a non-integer number, a discriminant that isn't a string on every case, or cases it
    /// can't tell apart, which is IOP2003. A union that is only <paramref name="written"/> (a parameter's, R2) may have an
    /// entity case, which needs no scope to be written, and drops its cases outside the selection; it's never read.
    /// </summary>
    private Unmappable? UnionProblem(IrType type, string? name, bool written)
    {
        var all = Values(type);
        var label = name is null ? "a union" : $"the union `{name}`";
        var ambiguous = name is null ? "an ambiguous union" : $"the ambiguous union `{name}`";
        if (!written && all.FirstOrDefault(IsUnselected) is RefType unselected)
        {
            return new($"class `{unselected.Name}` outside the selection");
        }
        var flat = all.Where(t => !IsUnselected(t)).ToList(); // as Union() drops them
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
        if (flat.Count < 2)
        {
            return new($"{label}, whose other cases are outside the selection");
        }
        var cases = new List<(string? Name, string? FullName, JsonKind Json, Declaration? Declaration)>();
        foreach (var t in flat)
        {
            if (t is PrimitiveType { Name: "any" or "unknown" or "object" } any)
            {
                return new($"{label} with `{any.Name}` (§2)");
            }
            if (IsElement(t))
            {
                return new($"{label} with a DOM element (R13)");
            }
            if (MapProblem(t, written) is { } problem)
            {
                return problem;
            }
            if (ValueType(t) is { } valueType)
            {
                return new($"{label} with the value-type case {valueType} (R2)");
            }
            if (TypeParameterIn(t) is { } parameter)
            {
                return new($"{label} with a case of the type parameter `{parameter.Name}` (§2)"); // a union struct isn't generic
            }
            if (!written && UsesEntity(t))
            {
                return new($"{label} with the case {NameOf(t) ?? "`this`"}, which needs a scope to be read (R17, R19)");
            }
            if (NameOf(t, full: true) is not { } caseName || cases.All(c => c.FullName != caseName)) // R5, as Cases dedupes
            {
                cases.Add((NameOf(t), NameOf(t, full: true), JsonOf(t), t is RefType r ? Find(r) : null));
            }
        }
        if (WrittenOnly(all, flat))
        {
            return null; // never read, so R19 doesn't apply
        }
        if (type is UnionType u && u.Types.Count(t => !IsNullish(t)) == all.Count && u.Discriminant is { } discriminant) // as Union() reads it
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

    /// <summary>A data record's members; one that is a DOM element is written only, and so is the record (R13).</summary>
    private Unmappable? RecordProblem(int indexSignatures, IEnumerable<PropertyMember> members) =>
        indexSignatures > 0 ? new("an index signature")
        : members.Where(p => !p.Static && p.Type is not LiteralType && !ElementOf(p.Type)).Select(p => Problem(p.Type, Position.Data)).FirstOrDefault(p => p is not null);

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
            EnumDeclaration e => Name(e).Name,
            AliasDeclaration { Type: not ObjectType } a => Values(a.Type) is [var single] ? ValueType(single) : Name(a).Name,
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// The name of the C# type <see cref="Map"/> gives, or null for an anonymous one; <paramref name="full"/>: its
    /// <see cref="CsType.FullName"/>, which tells same-named types of two namespaces apart.
    /// </summary>
    private string? NameOf(IrType type, bool full = false) => type switch
    {
        PrimitiveType { Name: "number" } => "double",
        PrimitiveType { Name: "string" } => "string",
        PrimitiveType { Name: "boolean" } => "bool",
        RefType { Module: not null } r => Find(r) switch
        {
            ClassDeclaration c => Spell(IsValueLike(c) ? Snapshot(c) : Name(c), full),
            AliasDeclaration { Type: not ObjectType } a when Values(a.Type) is [var single] => NameOf(single, full),
            { } d => Spell(Name(d), full),
            null => null,
        },
        ArrayType a => NameOf(a.Element, full) is { } element ? element + "[]" : null,
        TupleType { Required: null, Rest: false, Elements.Count: > 0 } t =>
            t.Elements.Select(e => NameOf(e, full)).Distinct().ToList() is [{ } element] ? element + "[]" : null,
        _ => null,
    };

    private static string Spell(TypeName name, bool full) => full ? name.FullName : name.Name;

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

    /// <summary>The declaration a reference names, as selected; null for the platform's types.</summary>
    public Declaration? Declaration(RefType type) => Find(type);

    /// <summary>What selection skips can't reach the mapping.</summary>
    private static UnreachableException Unreachable(string subject) => new($"{subject}: selection should have skipped this.");
}
