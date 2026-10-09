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
/// reader uses the caller's <c>scope</c>, so lambdas around it aren't static.
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

    /// <summary>A type with static <c>Write(writer, value)</c> and <c>Read(ref reader)</c> methods.</summary>
    public static CsType Method(string name, JsonKind json, string write, string read, bool isValueType = false) =>
        new(name, json, isValueType, write, read);

    /// <summary>A string enum and its name map (R9).</summary>
    public static CsType StringEnum(string name) =>
        new(name, JsonKind.String, true, $"{name}Names.Write", $"{name}Names.Read", fast: "String", names: $"{name}Names");

    /// <summary>A live class, written as its handle and read with the caller's scope (design §3.2).</summary>
    public static CsType Live(string name) => new(name, JsonKind.Object, false, $"{name}.Write", $"scope.Read<{name}>", scoped: true);

    /// <summary>A data or snapshot record (R15, R16).</summary>
    public static CsType Record(string name) => Method(name, JsonKind.Object, $"{name}.Write", $"{name}.Read");

    public static CsType Union(string name) =>
        new(name, JsonKind.Object, isValueType: true, "JsJson.WriteValue", $"JsJson.ReadUnion<{name}>", isUnion: true);

    /// <summary>A number-literal union or numeric enum: written as its value.</summary>
    public static CsType NumberEnum(string name) =>
        new(name, JsonKind.Number, isValueType: true, null, null,
            (w, v, _) => $"JsJson.WriteNumber({w}, (int){v})", (r, _) => $"({name})JsJson.ReadNumber(ref {r})");

    public static CsType Array(CsType element) =>
        new($"{element.Name}[]", JsonKind.Array, isValueType: false, null, null,
            (w, v, d) => $"JsJson.WriteArray({w}, {v}, {element.Writer(d)})", (r, d) => $"JsJson.ReadArray(ref {r}, {element.Reader(d)})",
            scoped: element.Scoped)
        { Element = element };

    private CsType? Element { get; init; }

    /// <summary>
    /// This type, calling its static methods through <c>global::{ns}</c> where a member of the type's name hides it
    /// (<paramref name="hidden"/>): in a record with <c>Opt&lt;Cursor&gt; Cursor</c>, <c>Cursor.Read</c> is that property (CS0120).
    /// </summary>
    public CsType Unhidden(IReadOnlySet<string> hidden, string ns)
    {
        if (Element is not null)
        {
            return Array(Element.Unhidden(hidden, ns));
        }
        if (!hidden.Contains(Name))
        {
            return this;
        }
        string? Qualify(string? method) => method?.StartsWith(Name + ".", StringComparison.Ordinal) == true ? $"global::{ns}.{method}" : method;
        return new(Name, Json, IsValueType, Qualify(writeMethod), Qualify(readMethod), write, read, IsUnion, Scoped, Fast, Names);
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
