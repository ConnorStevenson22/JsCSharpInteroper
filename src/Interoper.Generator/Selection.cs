using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// A declaration to emit, trimmed to what's included: a class keeps only its included members, events and signatures,
/// and a dropped optional parameter is gone from its signature (R22).
/// </summary>
/// <param name="Watches">The class's properties to generate <c>WatchX</c> for.</param>
public sealed record Selected(string Module, Declaration Declaration, IReadOnlyList<PropertyMember> Watches);

/// <summary>
/// What to emit for an IR and a profile. The profile's <c>include</c> lists live classes, their members, events and
/// watches, and module functions; absent, it lists everything but watches, which are emitted only where listed. Data types (interfaces, aliases, enums) come in whole
/// when an included member uses them. A member that uses an unlisted class, or a type that can't be mapped yet, is
/// skipped with IOP2007; an optional trailing parameter is dropped instead (R22).
/// </summary>
public sealed class Selection
{
    private static readonly HashSet<string> SupportedPrimitives = ["string", "number", "boolean", "void", "undefined", "null"];

    private readonly Profile profile;
    private readonly Dictionary<(string Module, string Name), Declaration> byName = [];
    private readonly Dictionary<Declaration, string> moduleOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Declaration, Selected> selected = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ClassDeclaration> classes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Declaration, string?> problems = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Declaration> checking = new(ReferenceEqualityComparer.Instance);
    private readonly List<Diagnostic> diagnostics = [];
    private bool recursed;

    private Selection(IrDocument ir, Profile profile)
    {
        this.profile = profile;
        if (profile.Package != ir.Package.Name)
        {
            throw new InvalidDataException($"The profile is for {profile.Package}, the IR for {ir.Package.Name}.");
        }
        var all = ir.Modules.SelectMany(m => m.Declarations.Select(d => (m.Specifier, Declaration: d))).ToList();
        foreach (var (module, declaration) in all)
        {
            byName[(module, declaration.Name)] = declaration;
            moduleOf[declaration] = module;
        }

        var included = Included(all.Select(x => x.Declaration));
        foreach (var (declaration, _) in included)
        {
            if (declaration is ClassDeclaration { TypeParameters.Count: 0 } c)
            {
                classes.Add(c);
            }
        }
        foreach (var (declaration, listed) in included)
        {
            Add(declaration, listed);
        }
        Declarations = [.. all.Where(x => selected.ContainsKey(x.Declaration)).Select(x => selected[x.Declaration])];
    }

    /// <summary>The declarations to emit, in IR order.</summary>
    public IReadOnlyList<Selected> Declarations { get; }

    /// <summary>What was skipped or dropped (IOP2007), each once.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    public static Selection Select(IrDocument ir, Profile profile) => new(ir, profile);

    /// <summary>The members, events and watches <c>include</c> lists from a class it doesn't include whole.</summary>
    private sealed record Listed(HashSet<string> Members, HashSet<string> Events, HashSet<string> Watches);

    /// <summary>The declarations the profile lists, in IR order, each with its listed parts (<c>null</c>: whole).</summary>
    private List<(Declaration Declaration, Listed? Listed)> Included(IEnumerable<Declaration> all)
    {
        if (profile.Include is not { } include)
        {
            return [.. all.Select(d => (d, (Listed?)null))];
        }
        var whole = new HashSet<string>();
        var parts = new Dictionary<string, Listed>();
        foreach (var entry in include)
        {
            if (entry.Split('.', 2) is not [var name, var part])
            {
                whole.Add(entry);
                continue;
            }
            var listed = parts.TryGetValue(name, out var l) ? l : parts[name] = new([], [], []);
            switch (part.Split(':', 2))
            {
                case ["on", var e]:
                    listed.Events.Add(e);
                    break;
                case ["watch", var p]:
                    listed.Watches.Add(p);
                    break;
                default:
                    listed.Members.Add(part);
                    break;
            }
        }
        var result = all.Where(d => whole.Contains(d.Name) || parts.ContainsKey(d.Name))
            .Select(d => (d, whole.Contains(d.Name) ? null : parts[d.Name])).ToList();
        if (whole.Concat(parts.Keys).Except(result.Select(x => x.d.Name)).FirstOrDefault() is { } unknown)
        {
            throw new InvalidDataException($"Profile include: {unknown} isn't a declaration of {profile.Package}.");
        }
        foreach (var (declaration, listed) in result)
        {
            if (listed is not null)
            {
                Validate(declaration, listed);
            }
        }
        return result;
    }

    private static void Validate(Declaration declaration, Listed listed)
    {
        if (declaration is not ClassDeclaration c)
        {
            throw new InvalidDataException($"Profile include: {declaration.Name} isn't a class, so it's included whole.");
        }
        var missing = listed.Members.Where(m => !c.Members.Any(x => x.Name == m))
            .Concat(listed.Events.Where(e => !c.Events.Any(x => x.Name == e)).Select(e => "on:" + e))
            .Concat(listed.Watches.Where(p => !c.Members.Any(x => x is PropertyMember && x.Name == p)).Select(p => "watch:" + p));
        if (missing.FirstOrDefault() is { } name)
        {
            throw new InvalidDataException($"Profile include: {c.Name} has no {name}.");
        }
    }

    private void Add(Declaration declaration, Listed? listed)
    {
        switch (declaration)
        {
            case ClassDeclaration { TypeParameters.Count: > 0 }:
                Skip(declaration.Name, $"generic `{declaration.Name}`");
                break;
            case ClassDeclaration c:
                AddClass(c, listed);
                break;
            case FunctionDeclaration f:
                var signatures = Signatures(f.Name, f.Signatures);
                if (signatures.Count > 0)
                {
                    selected[f] = new(moduleOf[f], f with { Signatures = signatures }, []);
                }
                break;
            case VariableDeclaration:
                Skip(declaration.Name, "a variable");
                break;
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

    private void AddClass(ClassDeclaration c, Listed? listed)
    {
        var members = new List<Member>();
        foreach (var member in c.Members.Where(m => listed?.Members.Contains(m.Name) ?? !IsConvention(m)))
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
            var types = property.WriteType is { } writeType ? [property.Type, writeType] : new[] { property.Type };
            if (FirstProblem(types) is { } problem)
            {
                Skip(subject, problem);
                continue;
            }
            foreach (var type in types)
            {
                Collect(type);
            }
            members.Add(property);
        }
        var events = new List<Event>();
        foreach (var e in c.Events.Where(e => listed?.Events.Contains(e.Name) ?? true))
        {
            if (profile.Events?.Subscribe is null)
            {
                Report(new(Diagnostic.NotSupported, $"{c.Name}.on:{e.Name}", "skipped: the profile names no subscribe method"));
                continue;
            }
            if (Problem(e.Type) is { } problem)
            {
                Skip($"{c.Name}.on:{e.Name}", problem);
                continue;
            }
            Collect(e.Type);
            events.Add(e);
        }
        var watches = new List<PropertyMember>();
        foreach (var p in c.Members.OfType<PropertyMember>().Where(p => listed?.Watches.Contains(p.Name) ?? false))
        {
            if (profile.Watch is null)
            {
                Report(new(Diagnostic.NotSupported, $"{c.Name}.watch:{p.Name}", "skipped: the profile names no watch method"));
                continue;
            }
            if (Problem(p.Type) is { } problem)
            {
                Skip($"{c.Name}.watch:{p.Name}", problem);
                continue;
            }
            Collect(p.Type);
            watches.Add(p);
        }
        selected[c] = new(moduleOf[c], c with { Members = members, Events = events }, watches);
    }

    /// <summary>
    /// The runtime-class property (never bound) and the event and watch methods (bound as events and watches): left out
    /// of a whole class without a diagnostic.
    /// </summary>
    private bool IsConvention(Member member) =>
        member.Name == profile.RuntimeClassProperty || member.Name == profile.Events?.Subscribe || member.Name == profile.Watch;

    /// <summary>
    /// A base's discriminant (<c>Shape.type</c>), which each subclass narrows to a literal: the live type says it, so it's
    /// no member, and its type (<c>ShapeType</c>) isn't emitted for it.
    /// </summary>
    private bool IsDiscriminant(ClassDeclaration c, PropertyMember property) =>
        moduleOf.Keys.OfType<ClassDeclaration>().Any(sub =>
            sub.Extends is RefType r && ReferenceEquals(Find(r), c) &&
            sub.Members.Any(m => m is PropertyMember { Type: LiteralType } && m.Name == property.Name));

    /// <summary>The signatures that can be mapped, without a rest parameter or trailing optional ones that can't (R22).</summary>
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
            var kept = signature.Params.TakeWhile(p => !p.Rest && Problem(p.Type) is null).ToList();
            if (kept.Count < signature.Params.Count)
            {
                var dropped = signature.Params[kept.Count];
                Report(new(Diagnostic.NotSupported, $"{subject}({dropped.Name})",
                    dropped.Rest ? "rest parameter dropped (R22)" : $"optional parameter dropped (R22): uses {Problem(dropped.Type)}"));
            }
            var trimmed = signature with { Params = kept };
            foreach (var type in trimmed.Params.Select(p => p.Type).Append(trimmed.Returns).OfType<IrType>())
            {
                Collect(type);
            }
            result.Add(trimmed);
        }
        return result;
    }

    /// <summary>Why a signature can't be mapped: a type parameter, its result, or a required parameter.</summary>
    private string? Problem(Signature signature) =>
        signature.TypeParameters.Count > 0 ? $"type parameter `{signature.TypeParameters[0].Name}`"
        : signature.Returns is { } returns && Problem(returns) is { } problem ? problem
        : signature.Params.FirstOrDefault(p => Problem(p.Type) is not null) is { Optional: false, Rest: false } required ? Problem(required.Type)
        : null;

    private void Skip(string subject, string problem) => Report(new(Diagnostic.NotSupported, subject, $"skipped: uses {problem}"));

    /// <summary>Adds a diagnostic unless it's already there, as when two overloads are skipped for the same reason.</summary>
    private void Report(Diagnostic diagnostic)
    {
        if (!diagnostics.Contains(diagnostic))
        {
            diagnostics.Add(diagnostic);
        }
    }

    // --- What can be mapped ---

    /// <summary>Why a type can't be mapped yet, or <c>null</c>.</summary>
    private string? Problem(IrType type) => type switch
    {
        PrimitiveType p => SupportedPrimitives.Contains(p.Name) ? null : $"`{p.Name}`",
        LiteralType or ThisType => null,
        RefType { Module: null, Name: "Promise" } r => FirstProblem(r.Args),
        RefType { Module: null } r => $"`{r.Name}`",
        RefType r => Find(r) is { } declaration ? DeclarationProblem(declaration) : $"`{r.Name}`",
        ObjectType o => MembersProblem(o.Members, o.CallSignatures.Count + o.ConstructSignatures.Count) ?? FirstProblem(Children(o)),
        FunctionType => "a callback",
        TypeParameterType t => $"type parameter `{t.Name}`",
        UnresolvedType u => $"unresolved `{u.Text}`",
        _ => FirstProblem(Children(type)),
    };

    private string? FirstProblem(IEnumerable<IrType> types) => types.Select(Problem).FirstOrDefault(p => p is not null);

    private static string? MembersProblem(IEnumerable<Member> members, int callSignatures) =>
        callSignatures > 0 ? "a callable object"
        : members.OfType<MethodMember>().FirstOrDefault() is { } method ? $"method `{method.Name}`"
        : null;

    /// <summary>A class can be mapped when it's emitted; a data type when everything in it can be.</summary>
    private string? DeclarationProblem(Declaration declaration)
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
        var problem = declaration switch
        {
            ClassDeclaration c => classes.Contains(c) ? null
                : c.TypeParameters.Count > 0 ? $"generic `{c.Name}`" : $"unlisted class `{c.Name}`",
            InterfaceDeclaration { TypeParameters.Count: > 0 } or AliasDeclaration { TypeParameters.Count: > 0 } => $"generic `{declaration.Name}`",
            InterfaceDeclaration i => MembersProblem(i.Members, i.CallSignatures.Count + i.ConstructSignatures.Count) ?? FirstProblem(Parts(i)),
            AliasDeclaration a => Problem(a.Type),
            EnumDeclaration => null,
            _ => $"`{declaration.Name}`",
        };
        if (problem is not null && declaration is not ClassDeclaration)
        {
            problem = $"{problem} (in `{declaration.Name}`)";
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
                child is RefType { Module: not null } nested && Find(nested) is AliasDeclaration { Type: UnionType inner })
            {
                Collect(inner);
                continue;
            }
            Collect(child);
        }
    }

    private void AddDataType(Declaration declaration)
    {
        if (selected.TryAdd(declaration, new(moduleOf[declaration], declaration, [])))
        {
            foreach (var type in Parts(declaration))
            {
                Collect(type);
            }
        }
    }

    private static bool IsNullish(IrType type) => type is PrimitiveType { Name: "null" or "undefined" };

    private Declaration? Find(RefType type) => byName.GetValueOrDefault((type.Module!, type.Name));

    /// <summary>The types a data type is made of.</summary>
    private static IEnumerable<IrType> Parts(Declaration declaration) => declaration switch
    {
        InterfaceDeclaration i => i.Extends.Concat(MemberTypes(i.Members, i.IndexSignatures)),
        AliasDeclaration a => [a.Type],
        _ => [],
    };

    /// <summary>The types a type is made of, one level down.</summary>
    private static IEnumerable<IrType> Children(IrType type) => type switch
    {
        RefType r => r.Args,
        ArrayType a => [a.Element],
        TupleType t => t.Elements,
        UnionType u => u.Types,
        IntersectionType i => i.Types,
        ObjectType o => MemberTypes(o.Members, o.IndexSignatures),
        _ => [],
    };

    private static IEnumerable<IrType> MemberTypes(IEnumerable<Member> members, IEnumerable<IndexSignature> indexSignatures) =>
        members.OfType<PropertyMember>().SelectMany(p => p.WriteType is { } w ? [p.Type, w] : new[] { p.Type })
            .Concat(indexSignatures.Select(i => i.Type));
}
