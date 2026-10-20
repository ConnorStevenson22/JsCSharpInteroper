using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interoper.Generator.Ir;

// IR v1 (docs/ir.md), as the extractor writes it. Absent lists are empty; absent flags are false.

/// <summary>One or more packages' IR: the generator's input. <see cref="Package"/> is the first one.</summary>
public sealed record IrDocument
{
    public required int Version { get; init; }
    public required IrPackage Package { get; init; }
    public IReadOnlyList<IrModule> Modules { get; init; } = [];
    public IReadOnlyList<Element> Elements { get; init; } = [];

    public static IrDocument Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads the version first, so a newer IR fails on its version rather than on a field this one lacks.</summary>
    public static IrDocument Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1
            ? document.RootElement.Deserialize<IrDocument>(Json.Options)!
            : throw new InvalidDataException($"IR version {version} isn't supported; expected 1.");
    }
}

public sealed record IrPackage(string Name, string Version);

/// <param name="Specifier">How it's imported: through its package's <c>exports</c>, or its path in the package.</param>
public sealed record IrModule(string Specifier, IReadOnlyList<Declaration> Declarations)
{
    /// <summary>Its package, when that isn't the IR's (<see cref="IrDocument.Package"/>).</summary>
    public string? Package { get; init; }

    /// <summary>
    /// Its <c>.js</c> file's path in its package, when that isn't <see cref="Specifier"/>'s subpath (an export maps it):
    /// an import map entry of its own (design §3.5).
    /// </summary>
    public string? File { get; init; }

    /// <summary>No export of its package reaches it, so it can't be imported by <see cref="Specifier"/>.</summary>
    public bool Internal { get; init; }
}

public sealed record Doc(string? Summary = null, string? Default = null, string? Deprecated = null, string? Since = null, IReadOnlyList<string>? See = null);

// --- Declarations ---

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ClassDeclaration), "class")]
[JsonDerivedType(typeof(InterfaceDeclaration), "interface")]
[JsonDerivedType(typeof(AliasDeclaration), "alias")]
[JsonDerivedType(typeof(EnumDeclaration), "enum")]
[JsonDerivedType(typeof(FunctionDeclaration), "function")]
[JsonDerivedType(typeof(VariableDeclaration), "variable")]
public abstract record Declaration
{
    /// <summary>The declaration's own name, which a default export has too.</summary>
    public required string Name { get; init; }
    /// <summary>The name the module exports it under, when that isn't <see cref="Name"/>: <c>default</c>.</summary>
    public string? Export { get; init; }
    /// <summary>For a declaration of an internal module: the module that re-exports it, where it's imported from.</summary>
    public ImportFrom? ImportFrom { get; init; }
    public Doc? Doc { get; init; }
}

public sealed record ClassDeclaration : Declaration
{
    public bool Abstract { get; init; }
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public IrType? Extends { get; init; }
    /// <summary>A mixin base's other exported classes (ir.md): each is an interface the live class implements (§5).</summary>
    public IReadOnlyList<IrType> Mixins { get; init; } = [];
    public IReadOnlyList<IrType> Implements { get; init; } = [];
    public IReadOnlyList<Signature> Constructors { get; init; } = [];
    public IReadOnlyList<Member> Members { get; init; } = [];
    public IReadOnlyList<Event> Events { get; init; } = [];

    /// <summary>
    /// Not in the IR: an interface with methods, which the generator maps as a live class (R15).
    /// Its objects are plain JS objects, so it has no constructor, runtime class name or snapshot.
    /// </summary>
    [JsonIgnore]
    public bool Interface { get; init; }
}

public sealed record InterfaceDeclaration : Declaration
{
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public IReadOnlyList<IrType> Extends { get; init; } = [];
    public IReadOnlyList<Member> Members { get; init; } = [];
    public IReadOnlyList<IndexSignature> IndexSignatures { get; init; } = [];
    public IReadOnlyList<Signature> CallSignatures { get; init; } = [];
    public IReadOnlyList<Signature> ConstructSignatures { get; init; } = [];
}

public sealed record AliasDeclaration : Declaration
{
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public required IrType Type { get; init; }
}

public sealed record EnumDeclaration : Declaration
{
    public IReadOnlyList<EnumMember> Members { get; init; } = [];
}

public sealed record FunctionDeclaration : Declaration
{
    public IReadOnlyList<Signature> Signatures { get; init; } = [];
}

public sealed record VariableDeclaration : Declaration
{
    public required IrType Type { get; init; }
    public bool Readonly { get; init; }
}

/// <summary>
/// A module that re-exports a declaration, the name it exports it under when that isn't the declaration's, and its
/// file as <see cref="IrModule.File"/>'s.
/// </summary>
public sealed record ImportFrom(string Module, string? Export = null, string? File = null);

/// <summary>A TS enum member; <see cref="Value"/> is a string or a number.</summary>
public sealed record EnumMember(string Name, JsonElement Value, Doc? Doc = null);

// --- Members and signatures ---

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PropertyMember), "property")]
[JsonDerivedType(typeof(MethodMember), "method")]
public abstract record Member
{
    public required string Name { get; init; }
    public bool Optional { get; init; }
    public bool Static { get; init; }
    public bool Abstract { get; init; }
    public bool Inherited { get; init; }
}

public sealed record PropertyMember : Member
{
    public required IrType Type { get; init; }
    /// <summary>The setter's type, when an accessor pair's types differ.</summary>
    public IrType? WriteType { get; init; }
    public bool Readonly { get; init; }
    public Doc? Doc { get; init; }
}

public sealed record MethodMember : Member
{
    public IReadOnlyList<Signature> Signatures { get; init; } = [];
}

/// <summary>An event of the class's event map (R24).</summary>
public sealed record Event(string Name, IrType Type, Doc? Doc = null);

/// <summary>One overload (R23). Constructors have no <see cref="Returns"/>.</summary>
public sealed record Signature
{
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public IReadOnlyList<Parameter> Params { get; init; } = [];
    public IrType? Returns { get; init; }
    public Doc? Doc { get; init; }
}

public sealed record Parameter(string Name, IrType Type, bool Optional = false, bool Rest = false);

public sealed record TypeParameter(string Name, IrType? Constraint = null, IrType? Default = null);

public sealed record IndexSignature(IrType Key, IrType Type, bool Readonly = false);

// --- Types ---

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PrimitiveType), "primitive")]
[JsonDerivedType(typeof(LiteralType), "literal")]
[JsonDerivedType(typeof(RefType), "ref")]
[JsonDerivedType(typeof(TypeParameterType), "typeParameter")]
[JsonDerivedType(typeof(ThisType), "this")]
[JsonDerivedType(typeof(ArrayType), "array")]
[JsonDerivedType(typeof(TupleType), "tuple")]
[JsonDerivedType(typeof(UnionType), "union")]
[JsonDerivedType(typeof(IntersectionType), "intersection")]
[JsonDerivedType(typeof(ObjectType), "object")]
[JsonDerivedType(typeof(FunctionType), "function")]
[JsonDerivedType(typeof(UnresolvedType), "unresolved")]
public abstract record IrType;

public sealed record PrimitiveType(string Name) : IrType;

/// <summary>A string, number or boolean literal.</summary>
public sealed record LiteralType(JsonElement Value) : IrType;

/// <summary>A named type; <see cref="Module"/> is absent for the platform's own types (<c>Promise</c>).</summary>
public sealed record RefType(string Name, string? Module = null) : IrType
{
    public IReadOnlyList<IrType> Args { get; init; } = [];
}

public sealed record TypeParameterType(string Name) : IrType;

public sealed record ThisType : IrType;

public sealed record ArrayType(IrType Element) : IrType;

/// <summary><see cref="Required"/> is set when some elements are optional; <see cref="Rest"/> when the last repeats.</summary>
public sealed record TupleType(IReadOnlyList<IrType> Elements, int? Required = null, bool Rest = false) : IrType;

public sealed record UnionType(IReadOnlyList<IrType> Types, string? Discriminant = null) : IrType;

public sealed record IntersectionType(IReadOnlyList<IrType> Types) : IrType;

public sealed record ObjectType : IrType
{
    public IReadOnlyList<Member> Members { get; init; } = [];
    public IReadOnlyList<IndexSignature> IndexSignatures { get; init; } = [];
    public IReadOnlyList<Signature> CallSignatures { get; init; } = [];
    public IReadOnlyList<Signature> ConstructSignatures { get; init; } = [];
}

/// <summary>An object type with one call signature and nothing else.</summary>
public sealed record FunctionType : IrType
{
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public IReadOnlyList<Parameter> Params { get; init; } = [];
    public required IrType Returns { get; init; }
    public Doc? Doc { get; init; }
}

/// <summary>A type the checker couldn't resolve, as it spells it: what uses it is skipped (IOP2007, R12).</summary>
public sealed record UnresolvedType(string Text) : IrType;

// --- Custom elements ---

public sealed record Element
{
    public required string Tag { get; init; }
    /// <summary>A <see cref="RefType"/> to the element's class declaration.</summary>
    public required IrType Class { get; init; }
    public Doc? Doc { get; init; }
    public IReadOnlyList<ElementAttribute> Attributes { get; init; } = [];
    public IReadOnlyList<ElementEvent> Events { get; init; } = [];
    public IReadOnlyList<Slot> Slots { get; init; } = [];
}

/// <summary><see cref="Property"/> names the member the attribute reflects; <see cref="Type"/> is set only without one.</summary>
public sealed record ElementAttribute(string Name, string? Property = null, IrType? Type = null, Doc? Doc = null);

public sealed record ElementEvent(string Name, IrType? Detail = null, IrType? Type = null, Doc? Doc = null);

public sealed record Slot(string Name, Doc? Doc = null);
