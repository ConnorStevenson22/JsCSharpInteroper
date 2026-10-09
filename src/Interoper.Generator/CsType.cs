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
/// depth <c>d</c> names its parameters <c>w{d}</c>, <c>v{d}</c> and <c>r{d}</c> (no suffix at depth 0).
/// </summary>
internal sealed class CsType
{
    private readonly string? writeMethod, readMethod;
    private readonly Func<string, string, int, string>? write;
    private readonly Func<string, int, string>? read;

    private CsType(string name, JsonKind json, bool isValueType, string? writeMethod, string? readMethod,
        Func<string, string, int, string>? write = null, Func<string, int, string>? read = null, bool isUnion = false)
    {
        Name = name;
        Json = json;
        IsValueType = isValueType;
        IsUnion = isUnion;
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

    public static CsType Number { get; } = Method("double", JsonKind.Number, "JsJson.WriteNumber", "JsJson.ReadNumber", isValueType: true);

    public static CsType String { get; } = Method("string", JsonKind.String, "JsJson.WriteString", "JsJson.ReadString");

    public static CsType Boolean { get; } = Method("bool", JsonKind.Boolean, "JsJson.WriteBoolean", "JsJson.ReadBoolean", isValueType: true);

    /// <summary>A type with static <c>Write(writer, value)</c> and <c>Read(ref reader)</c> methods.</summary>
    public static CsType Method(string name, JsonKind json, string write, string read, bool isValueType = false) =>
        new(name, json, isValueType, write, read);

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
            (w, v, d) => $"JsJson.WriteArray({w}, {v}, {element.Writer(d)})", (r, d) => $"JsJson.ReadArray(ref {r}, {element.Reader(d)})");

    /// <summary>An expression that writes <paramref name="value"/> with <paramref name="writer"/>.</summary>
    public string WriteCall(string writer, string value, int depth) =>
        write?.Invoke(writer, value, depth) ?? $"{writeMethod}({writer}, {value})";

    /// <summary>An expression that reads one with <paramref name="reader"/>.</summary>
    public string ReadCall(string reader, int depth) => read?.Invoke(reader, depth) ?? $"{readMethod}(ref {reader})";

    /// <summary>A <c>JsonWrite&lt;T&gt;</c>: a method group, or a lambda.</summary>
    public string Writer(int depth) => writeMethod ?? $"static (w{Suffix(depth)}, v{Suffix(depth)}) => {WriteCall($"w{Suffix(depth)}", $"v{Suffix(depth)}", depth + 1)}";

    /// <summary>A <c>JsonRead&lt;T&gt;</c>: a method group, or a lambda.</summary>
    public string Reader(int depth) => readMethod ?? $"static (ref r{Suffix(depth)}) => {ReadCall($"r{Suffix(depth)}", depth + 1)}";

    private static string Suffix(int depth) => depth == 0 ? "" : $"{depth}";
}
