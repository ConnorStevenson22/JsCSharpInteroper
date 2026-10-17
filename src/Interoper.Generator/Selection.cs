using System.Text.RegularExpressions;
using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// A declaration to emit, trimmed to what can be mapped: a class keeps only the members, events and signatures that
/// can be, a property whose setter can't be is read-only (§5), a dropped optional or rest parameter is gone from its
/// signature (R22), a data record or inline object type has no optional members that can't be (R15, R11), an event
/// payload's interface has no methods (R24), and any other interface no iterator methods. A callback alias is one only when
/// its inline records drop a member, so its uses map the trimmed callback: it has no C# type of its own (R21).
/// </summary>
public sealed record Selected(string Module, Declaration Declaration);

/// <summary>
/// A custom element's component (R28): the element, with the attributes and events that can be bound, and its class as
/// selected. An event's <see cref="ElementEvent.Detail"/> is null when it has none; an inline object type is an inline
/// record (R11) named after the event (<c>DemoMapZoomChangeDetail</c>).
/// </summary>
public sealed record SelectedElement(string Module, Element Element, ClassDeclaration Class);

/// <summary>
/// What to emit for an IR and a profile. The profile's <c>include</c> lists module globs (absent: every module). Every
/// export of a matching module is emitted, plus the bases of emitted classes and the data types they use.
/// A member that uses a type that can't be mapped is skipped with IOP2007 (IOP2003 for an ambiguous union), so a
/// skipped type takes its users with it (mapping §8). Optional trailing and rest parameters (R22), setters (§5), optional
/// record members (R15, R11), methods of event payload interfaces (R24) and iterator methods are dropped instead.
/// A union drops cases outside the selection (R2). Each use of <c>any</c>, <c>unknown</c> or <c>object</c> is reported
/// as IOP2001. Each custom element of an emitted class gets a component (R28). A declaration that can't be imported
/// (design §3.5) has no <c>Create</c>, function or component (IOP2007).
/// </summary>
public sealed class Selection
{
    /// <summary>The primitives that map to <c>JsAny</c> (mapping §2), each use reported as IOP2001.</summary>
    private static readonly string[] Untyped = ["any", "unknown", "object"];

    /// <summary>Design §3.5: a declaration whose module is internal, and that no reachable module re-exports.</summary>
    private const string Unimportable = "can't be imported (its module is internal)";

    private static readonly HashSet<string> SupportedPrimitives = ["string", "number", "boolean", "void", "undefined", "null", .. Untyped];

    private readonly Profile profile;
    private readonly Dictionary<(string Module, string Name), Declaration> byName = [];
    private readonly Dictionary<Declaration, string> moduleOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Declaration, Selected> selected = new(ReferenceEqualityComparer.Instance);
    private readonly IrDocument ir;
    private readonly List<(string Module, Declaration Declaration)> all;
    private readonly HashSet<ClassDeclaration> classes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ClassDeclaration, Unmappable> skippedClasses = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Declaration, Unmappable?> problems = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Declaration> checking = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<InterfaceDeclaration> payloads = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AliasDeclaration> callbacks = new(ReferenceEqualityComparer.Instance);
    private readonly List<SelectedElement> elements = [];
    /// <summary>The type parameters of the generic declaration being checked or added, which its types can use (mapping §2).</summary>
    private IReadOnlyList<TypeParameter> typeParameters = [];
    private readonly List<Diagnostic> diagnostics = [];
    private readonly TypeMap types;
    private bool recursed;

    private Selection(IrDocument ir, Profile profile)
    {
        this.ir = ir;
        this.profile = profile;
        if (profile.Package != ir.Package.Name)
        {
            throw new InvalidDataException($"The profile is for {profile.Package}, the IR for {ir.Package.Name}.");
        }
        all = [.. ir.Modules.SelectMany(m => m.Declarations.Select(d => (m.Specifier, d)))];
        foreach (var (module, declaration) in all)
        {
            byName[(module, declaration.Name)] = declaration;
            moduleOf[declaration] = module;
        }

        var included = Included();
        var roots = all.Where(x => included.Contains(x.Module)).Select(x => x.Declaration).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var c in roots.OfType<ClassDeclaration>().ToList())
        {
            for (var b = Base(c); b is not null; b = Base(b))
            {
                roots.Add(b);
            }
        }
        classes.UnionWith(roots.OfType<ClassDeclaration>().Where(c => c.TypeParameters.Count == 0 || profile.Collections.Contains(c.Name)));
        types = Checker();
        // R19 step 2: a closed base is read by a string discriminant; without one it's skipped, with what uses it.
        while (classes.FirstOrDefault(c => types.Problem(c) is not null) is { } closed)
        {
            skippedClasses[closed] = types.Problem(closed)!;
            classes.Remove(closed);
            types = Checker();
        }
        // R24: an interface an emitted class's event names as its payload, and its bases, are data records without their
        // methods and without the members they can't map, required ones too: a payload is only read.
        var payloadTypes = new Stack<IrType>(classes.SelectMany(c => c.Events).Select(e => e.Type));
        while (payloadTypes.TryPop(out var type))
        {
            if (type is RefType { Module: not null } r && Find(r) is InterfaceDeclaration i && payloads.Add(i))
            {
                foreach (var b in i.Extends)
                {
                    payloadTypes.Push(b);
                }
            }
        }
        if (payloads.Count > 0)
        {
            types = Checker();
        }
        foreach (var (_, declaration) in all.Where(x => roots.Contains(x.Declaration)))
        {
            Add(declaration);
        }
        foreach (var element in ir.Elements)
        {
            AddElement(element);
        }
        Declarations = [.. all.Where(x => selected.ContainsKey(x.Declaration)).Select(x => selected[x.Declaration])];
    }

    /// <summary>The declarations to emit, in IR order.</summary>
    public IReadOnlyList<Selected> Declarations { get; }

    /// <summary>The custom elements to make components of (R28), in IR order.</summary>
    public IReadOnlyList<SelectedElement> Elements => elements;

    /// <summary>The interfaces an emitted class's events deliver, and their bases (R24): they drop every member they can't map.</summary>
    public IReadOnlySet<InterfaceDeclaration> Payloads => payloads;

    /// <summary>What was skipped or dropped (IOP2007, IOP2003), each once.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    public static Selection Select(IrDocument ir, Profile profile) => new(ir, profile);

    /// <summary>
    /// The modules <c>include</c> matches: a glob is a module specifier in which <c>*</c> matches any characters,
    /// <c>/</c> included. A glob that matches no module is an error.
    /// </summary>
    private HashSet<string> Included()
    {
        var specifiers = ir.Modules.Select(m => m.Specifier).ToList();
        if (profile.Include is not { } include)
        {
            return [.. specifiers];
        }
        var result = new HashSet<string>();
        foreach (var glob in include)
        {
            var pattern = new Regex($"^{Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal)}$", RegexOptions.CultureInvariant);
            var matches = specifiers.Where(s => pattern.IsMatch(s)).ToList();
            if (matches.Count == 0)
            {
                throw new InvalidDataException($"Profile include: {glob} matches no module of {profile.Package}.");
            }
            result.UnionWith(matches);
        }
        return result;
    }

    /// <summary>
    /// A type map over the IR and the classes to emit, for its mapping checks. They compare namespaces, not spell them, so
    /// any root namespace does.
    /// </summary>
    private TypeMap Checker() =>
        new(ir, profile, all.Where(x => x.Declaration is ClassDeclaration c && classes.Contains(c)).Select(x => new Selected(x.Module, x.Declaration)), [], ns: "", payloads);

    private ClassDeclaration? Base(ClassDeclaration c) => c.Extends is RefType { Module: not null } r ? Find(r) as ClassDeclaration : null;

    private void Add(Declaration declaration)
    {
        switch (declaration)
        {
            case ClassDeclaration c when !classes.Contains(c):
                Skip(c.Name, DeclarationProblem(c)!);
                break;
            case ClassDeclaration c:
                AddClass(c);
                break;
            case FunctionDeclaration f when types.Import(f) is null:
                Report(new(Diagnostic.NotSupported, f.Name, $"skipped: it {Unimportable}"));
                break;
            case FunctionDeclaration f:
                var signatures = Signatures(f.Name, f.Signatures);
                if (signatures.Count > 0)
                {
                    selected[f] = new(moduleOf[f], f with { Signatures = signatures });
                }
                break;
            case VariableDeclaration:
                Skip(declaration.Name, new("a variable"));
                break;
            case InterfaceDeclaration i when IsEventMap(i):
                break; // R28: the component binds the element's events from the manifest

            default:
                if (DeclarationProblem(declaration) is { } problem)
                {
                    Skip(declaration.Name, problem);
                }
                else
                {
                    AddDataType(declaration);
                }
                break;
        }
    }

    private void AddClass(ClassDeclaration c)
    {
        typeParameters = c.TypeParameters;
        if (!c.Abstract && !types.Constructible(c))
        {
            Report(new(Diagnostic.NotSupported, $"{c.Name}.constructor", types.Import(c) is not null
                ? "skipped: its parameter isn't a bag, so there's no Create (R18)"
                : $"skipped: it {Unimportable}, so there's no Create (R18){(types.IsValueLike(c) ? " and it can't be sent as a value (R16)" : "")}"));
        }
        var members = new List<Member>();
        foreach (var member in c.Members.Where(m => !IsConvention(m)))
        {
            var subject = $"{c.Name}.{member.Name}";
            if (member.Static)
            {
                Report(new(Diagnostic.NotSupported, subject, "skipped: a static member"));
                continue;
            }
            if (member is MethodMember method)
            {
                var signatures = Signatures(subject, method.Signatures);
                if (signatures.Count > 0)
                {
                    members.Add(method with { Signatures = signatures });
                }
                continue;
            }
            var property = (PropertyMember)member;
            if (IsDiscriminant(c, property))
            {
                continue;
            }
            // §5: a setter whose wider type can't be mapped is dropped, and the getter kept; it's read-only then.
            if (property.WriteType is { } writeType && !types.IsCallback(property) && !types.IsElement(property) &&
                (Problem(property.Type) ?? types.Problem(property with { WriteType = null })) is null &&
                (Problem(writeType) ?? types.Problem(property)) is { } setter)
            {
                Report(new(Diagnostic.NotSupported, subject, $"setter dropped: uses {setter}"));
                property = property with { WriteType = null, Readonly = true };
            }
            var parts = property.WriteType is { } kept ? [property.Type, kept] : new[] { property.Type };
            if ((FirstProblem(parts) ?? types.Problem(property)) is { } problem)
            {
                Skip(subject, problem);
                continue;
            }
            property = TrimInline(subject, property);
            foreach (var type in property.WriteType is { } trimmed ? [property.Type, trimmed] : new[] { property.Type })
            {
                Collect(type);
                ReportUntyped(subject, type);
            }
            members.Add(property);
        }
        var events = new List<Event>();
        foreach (var e in c.Events)
        {
            if (profile.Events?.Subscribe is null)
            {
                Report(new(Diagnostic.NotSupported, $"{c.Name}.on:{e.Name}", "skipped: the profile names no subscribe method"));
                continue;
            }
            if (TypeMap.IsVoid(e.Type))
            {
                events.Add(e); // R24: no payload, so a parameterless handler
                continue;
            }
            if ((Problem(e.Type) ?? types.Problem(e.Type, Position.Member)) is { } problem)
            {
                Skip($"{c.Name}.on:{e.Name}", problem);
                continue;
            }
            var payload = TrimInline($"{c.Name}.on:{e.Name}", e.Type);
            Collect(payload);
            ReportUntyped($"{c.Name}.on:{e.Name}", payload);
            events.Add(e with { Type = payload });
        }
        selected[c] = new(moduleOf[c], c with { Members = members, Events = events });
        typeParameters = [];
    }

    /// <summary>
    /// R28: an element's component, unless its class isn't emitted (or is generic). A class that isn't one of the IR's
    /// declarations (the extractor couldn't resolve the manifest's name) skips the component (IOP2007). An attribute or
    /// event that can't be bound is skipped (IOP2007), and so is an attribute without a property that isn't a primitive
    /// or a string enum; an attribute of a property that was skipped goes with it.
    /// </summary>
    private void AddElement(Element element)
    {
        if (element.Class is not RefType { Module: not null } declared || Find(declared) is not ClassDeclaration c)
        {
            var name = element.Class switch { UnresolvedType u => u.Text, RefType x => x.Name, _ => "?" };
            Report(new(Diagnostic.NotSupported, $"<{element.Tag}>", $"skipped: its class `{name}` isn't a class the IR declares (R28)"));
            return;
        }
        if (!selected.TryGetValue(c, out var s) || c.TypeParameters.Count > 0)
        {
            return;
        }
        if (types.Import(c) is null)
        {
            Report(new(Diagnostic.NotSupported, $"<{element.Tag}>", $"skipped: its class {Unimportable}, so nothing defines the element (R28)"));
            return;
        }
        var component = Naming.Type(element.Tag);
        var properties = new HashSet<string>();
        for (var k = c; k is not null; k = Base(k))
        {
            if (selected.TryGetValue(k, out var emitted))
            {
                properties.UnionWith(((ClassDeclaration)emitted.Declaration).Members.OfType<PropertyMember>().Select(p => p.Name));
            }
        }
        var attributes = new List<ElementAttribute>();
        foreach (var a in element.Attributes)
        {
            if (a.Property is { } property)
            {
                if (properties.Contains(property))
                {
                    attributes.Add(a);
                }
                continue;
            }
            var subject = $"<{element.Tag}> {a.Name}";
            if (a.Type is not { } type)
            {
                Report(new(Diagnostic.NotSupported, subject, "skipped: an attribute with neither a property nor a type (R28)"));
            }
            else if ((Problem(type) ?? types.Problem(type, Position.Parameter)) is { } problem)
            {
                Skip(subject, problem);
            }
            else if (!types.IsAttribute(type, new("", component + Naming.Pascal(a.Name))))
            {
                Report(new(Diagnostic.NotSupported, subject, "skipped: an attribute without a property is a string, number, boolean or string enum (R28)"));
            }
            else
            {
                Collect(type);
                attributes.Add(a);
            }
        }
        var events = new List<ElementEvent>();
        foreach (var e in element.Events)
        {
            var subject = $"<{element.Tag}> on:{e.Name}";
            var detail = e.Detail is { } d && Flatten(d).Any(t => !IsNullish(t) && t is not PrimitiveType { Name: "void" }) ? d : null;
            if (detail is not null && (Problem(detail) ?? types.Problem(detail, Position.Member)) is { } problem)
            {
                Skip(subject, problem);
                continue;
            }
            if (detail is not null)
            {
                detail = TrimInline(subject, detail);
                Collect(detail);
                ReportUntyped(subject, detail);
            }
            events.Add(e with { Detail = detail });
        }
        elements.Add(new(s.Module, element with { Attributes = attributes, Events = events }, (ClassDeclaration)s.Declaration));
    }

    /// <summary>
    /// R28: an element's event map, such as <c>DemoMapElementEventMap</c>: an interface whose members are all events
    /// of one element. Its types are DOM events (<c>CustomEvent&lt;T&gt;</c>), and the component binds the events from
    /// the manifest instead.
    /// </summary>
    private bool IsEventMap(InterfaceDeclaration i) =>
        i.Members.Count > 0 && i.Members.All(m => m is PropertyMember { Type: RefType { Module: null } }) &&
        ir.Elements.Any(e => i.Members.All(m => e.Events.Any(x => x.Name == m.Name)));

    /// <summary>A union's members, with nested unions in their place; any other type as it is.</summary>
    private static IEnumerable<IrType> Flatten(IrType type) => type is UnionType u ? u.Types.SelectMany(Flatten) : [type];

    /// <summary>R6: a union's members, with nested unions and the members of union aliases in their place, as the type map flattens them.</summary>
    private IEnumerable<IrType> Cases(IrType type) => type switch
    {
        UnionType u => u.Types.SelectMany(Cases),
        _ when FlattenedAlias(type, Find) is { } inner => Cases(inner),
        _ => [type],
    };

    /// <summary>
    /// R6: the union a union alias stands for, which a union it's in flattens into itself (the type map's Flatten,
    /// Cases, TrimInline and Collect alike); null for any other type. A generic one was instantiated (§2), or is skipped.
    /// </summary>
    internal static UnionType? FlattenedAlias(IrType type, Func<RefType, Declaration?> find) =>
        type is RefType { Module: not null } r && find(r) is AliasDeclaration { TypeParameters.Count: 0, Type: UnionType inner } ? inner : null;

    /// <summary>
    /// The runtime-class property (never bound) and the event and watch methods (bound as events and watches): left out
    /// of a class without a diagnostic.
    /// </summary>
    private bool IsConvention(Member member) =>
        member.Name == profile.RuntimeClassProperty || member.Name == profile.Events?.Subscribe || member.Name == profile.Watch;

    /// <summary>
    /// A base's discriminant (<c>Shape.type</c>), which each subclass narrows to a literal: the live type says it, so it's
    /// no member.
    /// </summary>
    private bool IsDiscriminant(ClassDeclaration c, PropertyMember property) =>
        moduleOf.Keys.OfType<ClassDeclaration>().Any(sub =>
            sub.Extends is RefType r && ReferenceEquals(Find(r), c) &&
            sub.Members.Any(m => m is PropertyMember { Type: LiteralType } && m.Name == property.Name));

    /// <summary>The signatures that can be mapped, without trailing optional or rest parameters that can't (R22).</summary>
    private List<Signature> Signatures(string subject, IEnumerable<Signature> signatures)
    {
        var result = new List<Signature>();
        foreach (var signature in signatures)
        {
            if (Problem(signature) is { } problem)
            {
                Skip(subject, problem);
                continue;
            }
            var kept = signature.Params.TakeWhile(p => ParameterProblem(p) is null).ToList();
            if (kept.Count < signature.Params.Count)
            {
                var dropped = signature.Params[kept.Count];
                Report(new(Diagnostic.NotSupported, $"{subject}({dropped.Name})",
                    $"{(dropped.Rest ? "rest" : "optional")} parameter dropped (R22): uses {ParameterProblem(dropped)}"));
            }
            var trimmed = signature with
            {
                Params = [.. kept.Select(p => p with { Type = TrimInline($"{subject}({p.Name})", p.Type) })],
                Returns = signature.Returns is { } returned ? TrimInline(subject, returned) : null,
            };
            foreach (var type in trimmed.Params.Select(p => p.Type).Append(trimmed.Returns).OfType<IrType>())
            {
                Collect(type);
            }
            foreach (var p in trimmed.Params)
            {
                ReportUntyped($"{subject}({p.Name})", p.Type);
            }
            if (trimmed.Returns is { } returns)
            {
                ReportUntyped(subject, returns);
            }
            result.Add(trimmed);
        }
        return result;
    }

    /// <summary>Why a signature can't be mapped: a type parameter, its result, or a required parameter.</summary>
    private Unmappable? Problem(Signature signature) =>
        signature.TypeParameters.Count > 0 ? new($"type parameter `{signature.TypeParameters[0].Name}`")
        : signature.Returns is { } returns && (Problem(returns) ?? types.ResultProblem(returns)) is { } problem ? problem
        : signature.Params.FirstOrDefault(p => ParameterProblem(p) is not null) is { Optional: false, Rest: false } required ? ParameterProblem(required)
        : null;

    /// <summary>R22: a rest parameter is <c>params T[]</c>, so its type must be an array.</summary>
    private Unmappable? ParameterProblem(Parameter p) =>
        p.Rest && p.Type is not ArrayType ? new("a type that isn't an array") : Problem(p.Type) ?? types.Problem(p.Type, Position.Parameter);

    private void Skip(string subject, Unmappable problem) => Report(new(problem.Code, subject, $"skipped: uses {problem}"));

    /// <summary>Adds a diagnostic unless it's already there, as when two overloads are skipped for the same reason.</summary>
    private void Report(Diagnostic diagnostic)
    {
        if (!diagnostics.Contains(diagnostic))
        {
            diagnostics.Add(diagnostic);
        }
    }

    // --- What can be reached ---

    /// <summary>
    /// Why a type can't be used: something outside the selection or the generator, here or in a declaration it names,
    /// or a type parameter that isn't the generic declaration's own. What the types themselves map to is the type map's
    /// to check (<see cref="TypeMap.Problem(IrType, Position)"/>).
    /// </summary>
    private Unmappable? Problem(IrType type) => type switch
    {
        PrimitiveType p => SupportedPrimitives.Contains(p.Name) ? null : new($"`{p.Name}`"),
        LiteralType or ThisType => null,
        RefType { Module: null, Name: "Promise" } r => FirstProblem(r.Args),
        // R2: a union drops a case that is a class outside the selection; R6: a union alias in it is flattened, so its
        // own cases are what's checked, not the alias on its own (the type map checks the whole union).
        UnionType u when u.Types.Count(t => !IsNullish(t)) > 1 => FirstProblem(Cases(u).Where(t => !types.IsUnselected(t))),
        RefType { Module: null } r => TypeMap.IsElement(r) ? null : new($"`{r.Name}`"), // R13: a DOM element is an ElementReference
        RefType r => Find(r) is { } declaration ? DeclarationProblem(declaration) ?? FirstProblem(r.Args) : new($"`{r.Name}`"),
        // R11, R15: an inline record drops an optional member that can't be mapped (TrimInline); it isn't generic (§2).
        ObjectType o => MembersProblem(o.Members, o.CallSignatures.Count + o.ConstructSignatures.Count)
            ?? FirstProblem(MemberTypes(o.Members.Where(m => m is PropertyMember { Optional: false }), o.IndexSignatures))
            ?? (TypeParameterIn(o) is { } parameter ? new($"an inline record with the type parameter `{parameter.Name}` (§2)") : null),
        TypeParameterType t => typeParameters.Any(p => p.Name == t.Name) ? null : new($"type parameter `{t.Name}`"),
        UnresolvedType u => new($"unresolved `{u.Text}`"),
        _ => FirstProblem(Children(type)),
    };

    /// <summary>The first type parameter a type names, if any.</summary>
    internal static TypeParameterType? TypeParameterIn(IrType type) =>
        type as TypeParameterType ?? Children(type).Select(TypeParameterIn).FirstOrDefault(t => t is not null);

    private Unmappable? FirstProblem(IEnumerable<IrType> types) => types.Select(Problem).FirstOrDefault(p => p is not null);

    /// <summary>
    /// An iterator method (<c>values(): IterableIterator&lt;T&gt;</c>, <c>[Symbol.iterator]()</c>), which iterators aren't
    /// mapped for: it's left out of an interface (IOP2007), so the interface can still be a data record (R15).
    /// </summary>
    private static bool IsIterator(Member member) =>
        member is MethodMember { Signatures.Count: > 0 } method && method.Signatures.All(s => s.Returns is RefType
        {
            Module: null,
            Name: "Iterator" or "IterableIterator" or "IteratorObject" or "Generator" or "ArrayIterator" or "MapIterator" or "SetIterator" or "StringIterator"
                or "AsyncIterator" or "AsyncIterableIterator" or "AsyncIteratorObject" or "AsyncGenerator",
        });

    private static Unmappable? MembersProblem(IEnumerable<Member> members, int callSignatures) =>
        callSignatures > 0 ? new("a callable object")
        : members.OfType<MethodMember>().FirstOrDefault() is { } method ? new($"method `{method.Name}`")
        : null;

    /// <summary>A class can be used when it's emitted; a data type when everything in it can be, and it maps.</summary>
    private Unmappable? DeclarationProblem(Declaration declaration)
    {
        if (problems.TryGetValue(declaration, out var known))
        {
            return known;
        }
        if (!checking.Add(declaration))
        {
            recursed = true; // a recursive data type is fine, unless something else in it isn't
            return null;
        }
        var outer = typeParameters;
        typeParameters = declaration is InterfaceDeclaration generic ? generic.TypeParameters : [];
        var problem = declaration switch
        {
            ClassDeclaration c => classes.Contains(c) ? null
                : skippedClasses.GetValueOrDefault(c)
                    ?? new(c.TypeParameters.Count > 0 && !profile.Collections.Contains(c.Name) ? $"generic `{c.Name}`" : $"class `{c.Name}` outside the selection"),
            AliasDeclaration { TypeParameters.Count: > 0 } => new($"generic `{declaration.Name}`"),
            InterfaceDeclaration i when Trim(i) is var t => MembersProblem(payloads.Contains(i) ? t.Members.OfType<PropertyMember>() : t.Members.Where(m => !IsIterator(m)), i.CallSignatures.Count + i.ConstructSignatures.Count)
                ?? FirstProblem(Parts(t)) ?? types.Problem(t),
            AliasDeclaration a => Problem(Trim(a).Type) ?? types.Problem(a),
            EnumDeclaration e => types.Problem(e),
            _ => new($"`{declaration.Name}`"),
        };
        typeParameters = outer;
        if (problem is not null && declaration is not ClassDeclaration)
        {
            problem = problem with { Text = $"{problem.Text} (in `{declaration.Name}`)" };
        }
        checking.Remove(declaration);
        // Inside a recursion, "fine" may only mean the declaration being checked further up wasn't known yet.
        if (problem is not null || !recursed || checking.Count == 0)
        {
            problems[declaration] = problem;
        }
        recursed &= checking.Count > 0;
        return problem;
    }

    // --- Reachable data types ---

    /// <summary>Adds the data types a mappable type uses.</summary>
    private void Collect(IrType type)
    {
        if (type is RefType { Module: not null } r && Find(r) is { } declaration and not ClassDeclaration)
        {
            AddDataType(declaration);
        }
        foreach (var child in Children(type))
        {
            // R6: a union alias inside a union is flattened into it, so it's emitted only where it's used on its own.
            if (type is UnionType u && u.Types.Count(t => !IsNullish(t)) > 1 &&
                FlattenedAlias(child, Find) is { } inner)
            {
                Collect(inner);
                continue;
            }
            Collect(child);
        }
    }

    private void AddDataType(Declaration declaration)
    {
        if (declaration is AliasDeclaration { Type: FunctionType } alias)
        {
            // R21: a callback alias has no C# type; its uses are delegates. Its inline records drop what they can't map
            // once, for every use (Visit(e).legs), so it's selected trimmed when they do.
            if (callbacks.Add(alias))
            {
                var trimmed = TrimInline(alias);
                if (!ReferenceEquals(trimmed, alias))
                {
                    selected[alias] = new(moduleOf[alias], trimmed);
                }
                Collect(((AliasDeclaration)trimmed).Type);
            }
            return;
        }
        if (selected.ContainsKey(declaration))
        {
            return;
        }
        foreach (var (member, problem) in Dropped(declaration))
        {
            Skip($"{declaration.Name}.{member.Name}", problem);
        }
        var emitted = TrimInline(Trim(declaration));
        if (emitted is InterfaceDeclaration i && payloads.Contains((InterfaceDeclaration)declaration))
        {
            foreach (var method in i.Members.OfType<MethodMember>())
            {
                Report(new(Diagnostic.NotSupported, $"{i.Name}.{method.Name}", "skipped: a method of an event payload (R24)"));
            }
            emitted = i with { Members = [.. i.Members.OfType<PropertyMember>()] };
        }
        else if (emitted is InterfaceDeclaration record && record.Members.Any(IsIterator))
        {
            foreach (var method in record.Members.Where(IsIterator).Cast<MethodMember>())
            {
                Skip($"{record.Name}.{method.Name}", Problem(method.Signatures[0].Returns!)!);
            }
            emitted = record with { Members = [.. record.Members.Where(m => !IsIterator(m))] };
        }
        selected[declaration] = new(moduleOf[declaration], emitted);
        foreach (var type in Parts(emitted))
        {
            Collect(type);
        }
        var fields = emitted switch
        {
            InterfaceDeclaration record => record.Members.OfType<PropertyMember>(),
            AliasDeclaration { Type: ObjectType o } => o.Members.OfType<PropertyMember>(),
            _ => [],
        };
        foreach (var p in fields)
        {
            ReportUntyped($"{declaration.Name}.{p.Name}", p.Type);
        }
    }

    private static bool IsNullish(IrType type) => type is PrimitiveType { Name: "null" or "undefined" };

    /// <summary>
    /// R15: a data record's own optional members that can't be mapped, each with why: the record is emitted without them
    /// (IOP2007), as a required one skips it. An event payload's drop their required ones too (R24). Its bases drop their own.
    /// </summary>
    private List<(PropertyMember Member, Unmappable Problem)> Dropped(Declaration declaration)
    {
        var members = declaration switch
        {
            InterfaceDeclaration i => i.Members,
            AliasDeclaration { Type: ObjectType o } => o.Members,
            _ => [],
        };
        var outer = typeParameters;
        typeParameters = declaration is InterfaceDeclaration generic ? generic.TypeParameters : [];
        var dropped = new List<(PropertyMember, Unmappable)>();
        var payload = declaration is InterfaceDeclaration && payloads.Contains((InterfaceDeclaration)declaration);
        foreach (var p in members.OfType<PropertyMember>().Where(p => (p.Optional || payload) && !p.Static))
        {
            if ((Problem(p.Type) ?? types.DroppedProblem(p)) is { } problem)
            {
                dropped.Add((p, problem));
            }
        }
        typeParameters = outer;
        return dropped;
    }

    /// <summary>A data record without the members it drops (<see cref="Dropped"/>); any other declaration as it is.</summary>
    private T Trim<T>(T declaration) where T : Declaration
    {
        var dropped = Dropped(declaration).Select(d => d.Member).ToHashSet(ReferenceEqualityComparer.Instance);
        return dropped.Count == 0 ? declaration : declaration switch
        {
            InterfaceDeclaration i => (T)(Declaration)(i with { Members = [.. i.Members.Where(m => !dropped.Contains(m))] }),
            AliasDeclaration { Type: ObjectType o } a => (T)(Declaration)(a with { Type = o with { Members = [.. o.Members.Where(m => !dropped.Contains(m))] } }),
            _ => declaration,
        };
    }

    /// <summary>R11, R15: a data record whose members' inline object types drop what they can't map (<see cref="TrimInline(string, IrType)"/>).</summary>
    private Declaration TrimInline(Declaration declaration)
    {
        var outer = typeParameters;
        typeParameters = declaration is InterfaceDeclaration generic ? generic.TypeParameters : [];
        var result = declaration switch
        {
            InterfaceDeclaration i when Members(i.Name, i.Members) is { } members => i with { Members = members },
            AliasDeclaration { Type: ObjectType o } a when Members(a.Name, o.Members) is { } members => a with { Type = o with { Members = members } },
            AliasDeclaration { Type: not ObjectType } a when TrimInline(a.Name, a.Type) is var type && !ReferenceEquals(type, a.Type) => a with { Type = type },
            _ => declaration,
        };
        typeParameters = outer;
        return result;

        // The members with their inline object types trimmed, or null when nothing changes.
        List<Member>? Members(string name, IReadOnlyList<Member> members)
        {
            var trimmed = members.Select(m => m is PropertyMember p ? TrimInline($"{name}.{p.Name}", p) : m).ToList();
            return trimmed.SequenceEqual(members, ReferenceEqualityComparer.Instance) ? null : trimmed;
        }
    }

    private PropertyMember TrimInline(string subject, PropertyMember p)
    {
        var (type, writeType) = (TrimInline(subject, p.Type), p.WriteType is { } w ? TrimInline(subject, w) : null);
        return ReferenceEquals(type, p.Type) && ReferenceEquals(writeType, p.WriteType) ? p : p with { Type = type, WriteType = writeType };
    }

    /// <summary>
    /// R11, R15: a type whose inline object types leave out the optional members they can't map, each reported (IOP2007)
    /// as <c>subject.member</c>, as a data record does (<see cref="Dropped"/>); the same type when nothing is left out.
    /// </summary>
    private IrType TrimInline(string subject, IrType type)
    {
        switch (type)
        {
            case ObjectType o:
                var members = new List<Member>();
                foreach (var m in o.Members)
                {
                    if (m is PropertyMember { Optional: true, Static: false } p && (Problem(p.Type) ?? types.DroppedProblem(p)) is { } problem)
                    {
                        Skip($"{subject}.{p.Name}", problem);
                        continue;
                    }
                    members.Add(m is PropertyMember property ? TrimInline($"{subject}.{property.Name}", property) : m);
                }
                return members.SequenceEqual(o.Members, ReferenceEqualityComparer.Instance) ? o : o with { Members = members };
            case UnionType u when u.Types.Count(t => !IsNullish(t)) > 1:
                // R6: a union alias in it is flattened into it (Collect), so its own inline cases are trimmed here, in its place.
                var flattened = u.Types.Select(t => FlattenedAlias(t, Find) is { } inner && TrimInline(subject, inner) is var trimmedInner &&
                    !ReferenceEquals(trimmedInner, inner) ? trimmedInner : TrimInline(subject, t)).ToList();
                return flattened.SequenceEqual(u.Types, ReferenceEqualityComparer.Instance) ? u : u with { Types = flattened };
            case UnionType u:
                return Same(u.Types, out var cases) ? u : u with { Types = cases };
            case ArrayType a:
                return TrimInline(subject, a.Element) is var element && ReferenceEquals(element, a.Element) ? a : a with { Element = element };
            case TupleType t:
                return Same(t.Elements, out var elements) ? t : t with { Elements = elements };
            case RefType r:
                return Same(r.Args, out var args) ? r : r with { Args = args };
            case FunctionType f:
                // A callback's arguments are read and its result written as data, so their inline records drop members too.
                var parameters = f.Params.Select(p => TrimInline($"{subject}({p.Name})", p.Type) is var t && ReferenceEquals(t, p.Type) ? p : p with { Type = t }).ToList();
                var returned = TrimInline(subject, f.Returns);
                return parameters.SequenceEqual(f.Params, ReferenceEqualityComparer.Instance) && ReferenceEquals(returned, f.Returns) ? f
                    : f with { Params = parameters, Returns = returned };
            default:
                return type;
        }

        bool Same(IReadOnlyList<IrType> types, out List<IrType> trimmed)
        {
            trimmed = [.. types.Select(t => TrimInline(subject, t))];
            return trimmed.SequenceEqual(types, ReferenceEqualityComparer.Instance);
        }
    }

    /// <summary>
    /// IOP2001: a member, parameter or result whose type has <c>any</c>, <c>unknown</c> or <c>object</c> in it, which
    /// is <c>JsAny</c> (mapping §2). The declarations it names report their own.
    /// </summary>
    private void ReportUntyped(string subject, IrType type)
    {
        if (Primitives(type).FirstOrDefault(Untyped.Contains) is { } name)
        {
            Report(new(Diagnostic.Untyped, subject, $"`{name}` is JsAny (§2)"));
        }

        static IEnumerable<string> Primitives(IrType type) =>
            type is PrimitiveType p ? [p.Name] : type is RefType { Module: not null } ? [] : Children(type).SelectMany(Primitives);
    }

    private Declaration? Find(RefType type) => byName.GetValueOrDefault((type.Module!, type.Name));

    /// <summary>The types a data type is made of.</summary>
    private static IEnumerable<IrType> Parts(Declaration declaration) => declaration switch
    {
        InterfaceDeclaration i => i.Extends.Concat(MemberTypes(i.Members, i.IndexSignatures)),
        AliasDeclaration a => [a.Type],
        _ => [],
    };

    /// <summary>The types a type is made of, one level down.</summary>
    internal static IEnumerable<IrType> Children(IrType type) => type switch
    {
        RefType r => r.Args,
        ArrayType a => [a.Element],
        TupleType t => t.Elements,
        UnionType u => u.Types,
        IntersectionType i => i.Types,
        ObjectType o => MemberTypes(o.Members, o.IndexSignatures),
        FunctionType f => f.Params.Select(p => p.Type).Append(f.Returns),
        _ => [],
    };

    private static IEnumerable<IrType> MemberTypes(IEnumerable<Member> members, IEnumerable<IndexSignature> indexSignatures) =>
        members.OfType<PropertyMember>().SelectMany(p => p.WriteType is { } w ? [p.Type, w] : new[] { p.Type })
            .Concat(indexSignatures.Select(i => i.Type));
}
