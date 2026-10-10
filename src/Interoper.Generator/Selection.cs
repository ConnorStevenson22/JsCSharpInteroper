using System.Text.RegularExpressions;
using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// A declaration to emit, trimmed to what can be mapped: a class keeps only the members, events and signatures that
/// can be, and a dropped optional parameter is gone from its signature (R22).
/// </summary>
public sealed record Selected(string Module, Declaration Declaration);

/// <summary>
/// What to emit for an IR and a profile. The profile's <c>include</c> lists module specifier globs (absent: every
/// module); every exported declaration of a matching module is emitted, and so are the bases of emitted classes and the
/// data types (interfaces, aliases, enums) they use, whole. What can't be mapped is found here, so that a skipped type
/// takes the members that use it along with it (mapping §8): a member that uses a class outside the selection, or a
/// type the type map can't map (<see cref="TypeMap.Problem(IrType, bool)"/>), is skipped with IOP2007, or IOP2003 for
/// an ambiguous union; an optional trailing parameter is dropped instead (R22).
/// </summary>
public sealed class Selection
{
    private static readonly HashSet<string> SupportedPrimitives = ["string", "number", "boolean", "void", "undefined", "null"];

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
        classes.UnionWith(roots.OfType<ClassDeclaration>().Where(c => c.TypeParameters.Count == 0));
        types = Checker();
        // R19 step 2: a closed base is read by a string discriminant; without one it's skipped, with what uses it.
        while (classes.FirstOrDefault(c => types.Problem(c) is not null) is { } closed)
        {
            skippedClasses[closed] = types.Problem(closed)!;
            classes.Remove(closed);
            types = Checker();
        }
        foreach (var (_, declaration) in all.Where(x => roots.Contains(x.Declaration)))
        {
            Add(declaration);
        }
        Declarations = [.. all.Where(x => selected.ContainsKey(x.Declaration)).Select(x => selected[x.Declaration])];
    }

    /// <summary>The declarations to emit, in IR order.</summary>
    public IReadOnlyList<Selected> Declarations { get; }

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

    /// <summary>A type map over the IR and the classes to emit, for its mapping checks.</summary>
    private TypeMap Checker() =>
        new(ir, profile, all.Where(x => x.Declaration is ClassDeclaration c && classes.Contains(c)).Select(x => new Selected(x.Module, x.Declaration)), []);

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
            case FunctionDeclaration f:
                var signatures = Signatures(f.Name, f.Signatures, live: false);
                if (signatures.Count > 0)
                {
                    selected[f] = new(moduleOf[f], f with { Signatures = signatures });
                }
                break;
            case VariableDeclaration:
                Skip(declaration.Name, new("a variable"));
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

    private void AddClass(ClassDeclaration c)
    {
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
                var signatures = Signatures(subject, method.Signatures, live: true);
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
            var parts = property.WriteType is { } writeType ? [property.Type, writeType] : new[] { property.Type };
            if ((FirstProblem(parts) ?? types.Problem(property)) is { } problem)
            {
                Skip(subject, problem);
                continue;
            }
            foreach (var type in parts)
            {
                Collect(type);
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
            if ((Problem(e.Type) ?? types.Problem(e.Type, live: true)) is { } problem)
            {
                Skip($"{c.Name}.on:{e.Name}", problem);
                continue;
            }
            Collect(e.Type);
            events.Add(e);
        }
        selected[c] = new(moduleOf[c], c with { Members = members, Events = events });
    }

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

    /// <summary>
    /// The signatures that can be mapped, without trailing optional parameters that can't (R22). <paramref name="live"/>:
    /// a live class's method, whose parameters and result can be entities.
    /// </summary>
    private List<Signature> Signatures(string subject, IEnumerable<Signature> signatures, bool live)
    {
        var result = new List<Signature>();
        foreach (var signature in signatures)
        {
            if (Problem(signature, live) is { } problem)
            {
                Skip(subject, problem);
                continue;
            }
            var kept = signature.Params.TakeWhile(p => !p.Rest && ParameterProblem(p, live) is null).ToList();
            if (kept.Count < signature.Params.Count)
            {
                var dropped = signature.Params[kept.Count];
                Report(new(Diagnostic.NotSupported, $"{subject}({dropped.Name})",
                    dropped.Rest ? "rest parameter dropped (R22)" : $"optional parameter dropped (R22): uses {ParameterProblem(dropped, live)}"));
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
    private Unmappable? Problem(Signature signature, bool live) =>
        signature.TypeParameters.Count > 0 ? new($"type parameter `{signature.TypeParameters[0].Name}`")
        : signature.Returns is { } returns && (Problem(returns) ?? types.ResultProblem(returns, live)) is { } problem ? problem
        : signature.Params.FirstOrDefault(p => ParameterProblem(p, live) is not null) is { Optional: false, Rest: false } required ? ParameterProblem(required, live)
        : null;

    private Unmappable? ParameterProblem(Parameter p, bool live) => Problem(p.Type) ?? types.Problem(p.Type, live);

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
    /// Why a type can't be used: something outside the selection or the generator, here or in a declaration it names.
    /// What the types themselves map to is the type map's to check (<see cref="TypeMap.Problem(IrType, bool)"/>).
    /// </summary>
    private Unmappable? Problem(IrType type) => type switch
    {
        PrimitiveType p => SupportedPrimitives.Contains(p.Name) ? null : new($"`{p.Name}`"),
        LiteralType or ThisType => null,
        RefType { Module: null, Name: "Promise" } r => FirstProblem(r.Args),
        RefType { Module: null } r => new($"`{r.Name}`"),
        RefType r => Find(r) is { } declaration ? DeclarationProblem(declaration) : new($"`{r.Name}`"),
        ObjectType o => MembersProblem(o.Members, o.CallSignatures.Count + o.ConstructSignatures.Count) ?? FirstProblem(Children(o)),
        FunctionType => new("a callback"),
        TypeParameterType t => new($"type parameter `{t.Name}`"),
        UnresolvedType u => new($"unresolved `{u.Text}`"),
        _ => FirstProblem(Children(type)),
    };

    private Unmappable? FirstProblem(IEnumerable<IrType> types) => types.Select(Problem).FirstOrDefault(p => p is not null);

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
        var problem = declaration switch
        {
            ClassDeclaration c => classes.Contains(c) ? null
                : skippedClasses.GetValueOrDefault(c) ?? new(c.TypeParameters.Count > 0 ? $"generic `{c.Name}`" : $"class `{c.Name}` outside the selection"),
            InterfaceDeclaration { TypeParameters.Count: > 0 } or AliasDeclaration { TypeParameters.Count: > 0 } => new($"generic `{declaration.Name}`"),
            InterfaceDeclaration i => MembersProblem(i.Members, i.CallSignatures.Count + i.ConstructSignatures.Count) ?? FirstProblem(Parts(i)) ?? types.Problem(i),
            AliasDeclaration a => Problem(a.Type) ?? types.Problem(a),
            EnumDeclaration e => types.Problem(e),
            _ => new($"`{declaration.Name}`"),
        };
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
        if (selected.TryAdd(declaration, new(moduleOf[declaration], declaration)))
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
