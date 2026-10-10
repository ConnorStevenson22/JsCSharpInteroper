namespace Interoper.Generator;

/// <summary>The JSON a value starts with, which tells a union's cases apart (R19 step 4).</summary>
internal enum JsonKind
{
    String,
    Number,
    Boolean,
    Array,
    Object,
}

/// <summary>
/// A C# type in generated code, and the expressions that read and write it as JSON. Expressions nest: a lambda made at
/// depth <c>d</c> names its parameters <c>w{d}</c>, <c>v{d}</c> and <c>r{d}</c> (no suffix at depth 0). A live type's
/// reader uses the caller's <c>scope</c>, so lambdas around it aren't static. A generated type has its plain name; code
/// in another namespace uses <see cref="In"/> (R27).
/// </summary>
internal sealed class CsType
{
    private readonly string? writeMethod, readMethod;
    private readonly Func<string, string, int, string>? write;
    private readonly Func<string, int, string>? read;

    private CsType(string name, JsonKind json, bool isValueType, string? writeMethod, string? readMethod,
        Func<string, string, int, string>? write = null, Func<string, int, string>? read = null, bool isUnion = false,
        bool scoped = false, string? fast = null, string? names = null)
    {
        Name = name;
        Json = json;
        IsValueType = isValueType;
        IsUnion = isUnion;
        Scoped = scoped;
        Fast = fast;
        Names = names;
        this.writeMethod = writeMethod;
        this.readMethod = readMethod;
        this.write = write;
        this.read = read;
    }

    public string Name { get; }

    /// <summary>A generated type's namespace (R27); none for the runtime's and the built-in types, and once qualified.</summary>
    public string? Namespace { get; private set; }

    /// <summary>The name with its namespace, the same from any namespace: what tells two types apart.</summary>
    public string FullName => Element is not null ? Element.FullName + "[]" : Namespace is null ? Name : new TypeName(Namespace, Name).FullName;

    /// <summary>Makes this type under another spelling of its name: <c>global::</c>-qualified, for <see cref="In"/>.</summary>
    private Func<string, CsType>? named;

    public JsonKind Json { get; }

    public bool IsValueType { get; }

    /// <summary>A generated union (R2): never wrapped in <c>Opt&lt;T&gt;</c>, since it has the undefined and null states itself.</summary>
    public bool IsUnion { get; }

    /// <summary>A live object (an entity, or <c>this</c>): reading one needs a scope (R17, §5).</summary>
    public bool Scoped { get; }

    /// <summary>The realm's typed fast path (design §3.4): <c>Number</c>, <c>String</c> or <c>Boolean</c>.</summary>
    public string? Fast { get; }

    /// <summary>A string enum's name map (R9), which converts it for the <c>String</c> fast path.</summary>
    public string? Names { get; }

    public static CsType Number { get; } = new("double", JsonKind.Number, true, "JsJson.WriteNumber", "JsJson.ReadNumber", fast: "Number");

    public static CsType String { get; } = new("string", JsonKind.String, false, "JsJson.WriteString", "JsJson.ReadString", fast: "String");

    public static CsType Boolean { get; } = new("bool", JsonKind.Boolean, true, "JsJson.WriteBoolean", "JsJson.ReadBoolean", fast: "Boolean");

    /// <summary>An extensible enum (R10): a string wrapper with static <c>Write</c> and <c>Read</c> methods.</summary>
    public static CsType Extensible(TypeName type) =>
        Named(type, name => new(name, JsonKind.String, true, $"{name}.Write", $"{name}.Read"));

    /// <summary>A string enum and its name map (R9).</summary>
    public static CsType StringEnum(TypeName type) =>
        Named(type, name => new(name, JsonKind.String, true, $"{name}Names.Write", $"{name}Names.Read", fast: "String", names: $"{name}Names"));

    /// <summary>A live class, written as its handle and read with the caller's scope (design §3.2).</summary>
    public static CsType Live(TypeName type) =>
        Named(type, name => new(name, JsonKind.Object, false, $"{name}.Write", $"scope.Read<{name}>", scoped: true));

    /// <summary>A data or snapshot record (R15, R16).</summary>
    public static CsType Record(TypeName type) => Named(type, name => new(name, JsonKind.Object, false, $"{name}.Write", $"{name}.Read"));

    public static CsType Union(TypeName type) =>
        Named(type, name => new(name, JsonKind.Object, isValueType: true, "JsJson.WriteValue", $"JsJson.ReadUnion<{name}>", isUnion: true));

    /// <summary>A number-literal union or numeric enum: written as its value.</summary>
    public static CsType NumberEnum(TypeName type) =>
        Named(type, name => new(name, JsonKind.Number, isValueType: true, null, null,
            (w, v, _) => $"JsJson.WriteNumber({w}, (int){v})", (r, _) => $"({name})JsJson.ReadNumber(ref {r})"));

    private static CsType Named(TypeName type, Func<string, CsType> make)
    {
        var result = make(type.Name);
        (result.Namespace, result.named) = (type.Namespace, make);
        return result;
    }

    public static CsType Array(CsType element) =>
        new($"{element.Name}[]", JsonKind.Array, isValueType: false, null, null,
            (w, v, d) => $"JsJson.WriteArray({w}, {v}, {element.Writer(d)})", (r, d) => $"JsJson.ReadArray(ref {r}, {element.Reader(d)})",
            scoped: element.Scoped)
        { Element = element };

    /// <summary>An array's element type.</summary>
    public CsType? Element { get; private init; }

    /// <summary>This type as code in namespace <paramref name="ns"/> names it: <c>global::</c>-qualified from another (R27).</summary>
    public CsType In(string ns) =>
        Element is not null ? Array(Element.In(ns))
        : Namespace is null || Namespace == ns ? this
        : named!(new TypeName(Namespace, Name).In(ns));

    /// <summary>
    /// This type, calling its static methods through <c>global::</c> where a member of the type's name hides it
    /// (<paramref name="hidden"/>): in a record with <c>Opt&lt;Cursor&gt; Cursor</c>, <c>Cursor.Read</c> is that property (CS0120).
    /// </summary>
    public CsType Unhidden(IReadOnlySet<string> hidden)
    {
        if (Element is not null)
        {
            return Array(Element.Unhidden(hidden));
        }
        if (Namespace is null || !hidden.Contains(Name))
        {
            return this;
        }
        string? Qualify(string? method) => method?.StartsWith(Name + ".", StringComparison.Ordinal) == true ? $"global::{Namespace}.{method}" : method;
        return new(Name, Json, IsValueType, Qualify(writeMethod), Qualify(readMethod), write, read, IsUnion, Scoped, Fast, Names) { Namespace = Namespace, named = named };
    }

    /// <summary>An expression that writes <paramref name="value"/> with <paramref name="writer"/>.</summary>
    public string WriteCall(string writer, string value, int depth) =>
        write?.Invoke(writer, value, depth) ?? $"{writeMethod}({writer}, {value})";

    /// <summary>An expression that reads one with <paramref name="reader"/>.</summary>
    public string ReadCall(string reader, int depth) => read?.Invoke(reader, depth) ?? $"{readMethod}(ref {reader})";

    /// <summary>A <c>JsonWrite&lt;T&gt;</c>: a method group, or a lambda.</summary>
    public string Writer(int depth) => writeMethod ?? $"static (w{Suffix(depth)}, v{Suffix(depth)}) => {WriteCall($"w{Suffix(depth)}", $"v{Suffix(depth)}", depth + 1)}";

    /// <summary>A <c>JsonRead&lt;T&gt;</c>: a method group, or a lambda.</summary>
    public string Reader(int depth) => readMethod ?? $"{Static}(ref r{Suffix(depth)}) => {ReadCall($"r{Suffix(depth)}", depth + 1)}";

    /// <summary>The modifier of a lambda that reads one: none when it uses the scope.</summary>
    public string Static => Scoped ? "" : "static ";

    private static string Suffix(int depth) => depth == 0 ? "" : $"{depth}";
}
