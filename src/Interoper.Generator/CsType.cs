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
/// reader, and a scoped record's (R17), use the caller's <c>scope</c>, so lambdas around them aren't static; so does a
/// callback's writer (R21). A generated type has its plain name; code in another namespace uses <see cref="In"/> (R27).
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

    /// <summary>A generated type's name without its type arguments.</summary>
    private string baseName = "";

    /// <summary>A generic type's type arguments (mapping §2).</summary>
    private IReadOnlyList<CsType> args = [];

    /// <summary>The full name of a type made of others (an array, a generic type, a callback), from theirs.</summary>
    private string? fullName;

    /// <summary>The name with its namespace, the same from any namespace: what tells two types apart.</summary>
    public string FullName => fullName ?? (Namespace is null ? Name : new TypeName(Namespace, Name).FullName);

    /// <summary>Makes this type under another spelling of its name: <c>global::</c>-qualified, for <see cref="In"/>.</summary>
    private Func<string, CsType>? named;

    /// <summary>A type made of others (an array, a callback): this type with each of them changed, for <see cref="In"/> and <see cref="Unhidden"/>.</summary>
    private Func<Func<CsType, CsType>, CsType>? rebuild;

    /// <summary>A callback's nullable form (<see cref="NullableCallback"/>), given whether its null is JS null.</summary>
    private Func<bool, CsType>? nullableCallback;

    public JsonKind Json { get; }

    public bool IsValueType { get; }

    /// <summary>A generated union (R2): never wrapped in <c>Opt&lt;T&gt;</c>, since it has the undefined and null states itself.</summary>
    public bool IsUnion { get; }

    /// <summary>
    /// A live object (an entity, a type parameter, or <c>this</c>), or a record holding one: reading one needs a scope
    /// (R17, §5).
    /// </summary>
    public bool Scoped { get; }

    /// <summary>A callback (R21): written with the caller's scope, which owns it, and never read.</summary>
    public bool IsCallback { get; private init; }

    /// <summary>A callback whose result JS uses (a <c>Func</c>), which async mode can't serve (R21, IOP2005).</summary>
    public bool Returns { get; private init; }

    /// <summary>The realm's typed fast path (design §3.4): <c>Number</c>, <c>String</c> or <c>Boolean</c>.</summary>
    public string? Fast { get; }

    /// <summary>A string enum's name map (R9), which converts it for the <c>String</c> fast path.</summary>
    public string? Names { get; }

    /// <summary>
    /// The string property a value type wraps, which a union keeps it as (R2): an extensible enum's <c>Value</c> (R10), a DOM
    /// element's <c>Id</c> (R13).
    /// </summary>
    public string? Wraps { get; private init; }

    public static CsType Number { get; } = new("double", JsonKind.Number, true, "global::Interoper.JsJson.WriteNumber", "global::Interoper.JsJson.ReadNumber", fast: "Number");

    public static CsType String { get; } = new("string", JsonKind.String, false, "global::Interoper.JsJson.WriteString", "global::Interoper.JsJson.ReadString", fast: "String");

    public static CsType Boolean { get; } = new("bool", JsonKind.Boolean, true, "global::Interoper.JsJson.WriteBoolean", "global::Interoper.JsJson.ReadBoolean", fast: "Boolean");

    /// <summary>
    /// <c>any</c>, <c>unknown</c> and <c>object</c> (mapping §2): the runtime's <c>JsAny</c>, read with the caller's scope,
    /// which owns the handles in it.
    /// </summary>
    public static CsType Any { get; } = new("global::Interoper.JsAny", JsonKind.Object, false, "global::Interoper.JsAny.Write", null,
        read: (r, _) => $"global::Interoper.JsAny.Read(ref {r}, scope)", scoped: true);

    /// <summary>
    /// A DOM element (R13): Blazor's <c>ElementReference</c>, written as <c>{"$e":id}</c> (in <c>Interoper.Blazor</c>) and
    /// never read.
    /// </summary>
    public static CsType DomElement { get; } = new("global::Microsoft.AspNetCore.Components.ElementReference", JsonKind.Object, true, "global::Interoper.JsElement.Write", null) { Wraps = "Id" };

    /// <summary>An extensible enum (R10): a string wrapper with static <c>Write</c> and <c>Read</c> methods.</summary>
    public static CsType Extensible(TypeName type) =>
        Named(type, [], name => new(name, JsonKind.String, true, $"{name}.Write", $"{name}.Read") { Wraps = "Value" });

    /// <summary>A string enum and its name map (R9).</summary>
    public static CsType StringEnum(TypeName type) =>
        Named(type, [], name => new(name, JsonKind.String, true, $"{name}Names.Write", $"{name}Names.Read", fast: "String", names: $"{name}Names"));

    /// <summary>
    /// A live class, written as its handle and read with the caller's scope (design §3.2). A generic one (a collection,
    /// mapping §2) isn't in the registry, so its reader makes it.
    /// </summary>
    public static CsType Live(TypeName type, IReadOnlyList<CsType>? args = null) => args is not { Count: > 0 }
        ? Named(type, [], name => new(name, JsonKind.Object, false, $"{name}.Write", $"scope.Read<{name}>", scoped: true))
        : Named(type, args, name => new(name, JsonKind.Object, false, $"{name}.Write", null,
            read: (r, _) => $"scope.Read(ref {r}, static (realm, handle) => new {name}(realm, handle))", scoped: true));

    /// <summary>A type parameter of a generic class or record: an entity (<c>where T : JsObject</c>, mapping §2).</summary>
    public static CsType TypeParameter(string name) => new(name, JsonKind.Object, false, "global::Interoper.JsObject.Write", $"scope.Read<{name}>", scoped: true);

    /// <summary>
    /// A data or snapshot record (R15, R16). A scoped one holds an entity, so it's read with the caller's scope (R17).
    /// </summary>
    public static CsType Record(TypeName type, IReadOnlyList<CsType>? args = null, bool scoped = false) => scoped
        ? Named(type, args ?? [], name => new(name, JsonKind.Object, false, $"{name}.Write", null, read: (r, _) => $"{name}.Read(ref {r}, scope)", scoped: true))
        : Named(type, args ?? [], name => new(name, JsonKind.Object, false, $"{name}.Write", $"{name}.Read"));

    public static CsType Union(TypeName type) =>
        Named(type, [], name => new(name, JsonKind.Object, isValueType: true, "global::Interoper.JsJson.WriteValue", $"global::Interoper.JsJson.ReadUnion<{name}>", isUnion: true));

    /// <summary>A number-literal union or numeric enum: written as its value.</summary>
    public static CsType NumberEnum(TypeName type) =>
        Named(type, [], name => new(name, JsonKind.Number, isValueType: true, null, null,
            (w, v, _) => $"global::Interoper.JsJson.WriteNumber({w}, (int){v})", (r, _) => $"({name})global::Interoper.JsJson.ReadNumber(ref {r})"));

    /// <summary><paramref name="make"/> makes the type from its name as code spells it: with its type arguments.</summary>
    private static CsType Named(TypeName type, IReadOnlyList<CsType> args, Func<string, CsType> make)
    {
        var result = make(Generic(type.Name, args.Select(a => a.Name)));
        (result.Namespace, result.baseName, result.args, result.named) = (type.Namespace, type.Name, args, make);
        if (args.Count > 0)
        {
            result.fullName = Generic(type.FullName, args.Select(a => a.FullName));
        }
        return result;
    }

    private static string Generic(string name, IEnumerable<string> args) => args.Any() ? $"{name}<{string.Join(", ", args)}>" : name;

    public static CsType Array(CsType element) =>
        new($"{element.Name}[]", JsonKind.Array, isValueType: false, null, null,
            (w, v, d) => $"global::Interoper.JsJson.WriteArray({w}, {v}, {element.Writer(d)})", (r, d) => $"global::Interoper.JsJson.ReadArray(ref {r}, {element.Reader(d)})",
            scoped: element.Scoped)
        { Element = element, fullName = element.FullName + "[]", rebuild = change => Array(change(element)) };

    /// <summary>An array or tuple element that can be null or undefined (R3): <c>Opt&lt;T&gt;</c>, undefined as <c>{"$u":1}</c>.</summary>
    public static CsType Opt(CsType value) =>
        new($"global::Interoper.Opt<{value.Name}>", value.Json, isValueType: true, null, null,
            (w, v, d) => $"global::Interoper.JsJson.WriteValue({w}, {v}, {value.Writer(d)})", (r, d) => $"global::Interoper.JsJson.ReadOpt(ref {r}, {value.Reader(d)})",
            scoped: value.Scoped)
        { fullName = $"global::Interoper.Opt<{value.FullName}>", rebuild = change => Opt(change(value)) };

    /// <summary>
    /// A callback (R21): an <c>Action</c>, or a <c>Func</c> when it has a result, with an argument per parameter (an
    /// optional or nullable one is <c>Opt&lt;T&gt;</c>, R3). It's registered with the caller's scope and written as
    /// <c>{"$f":id}</c>; a <paramref name="nullable"/> one (<c>Func&lt;…&gt;?</c>) writes null as JS null, or as undefined
    /// when <paramref name="jsNull"/> is false (an optional one whose type has no null).
    /// </summary>
    public static CsType Callback(IReadOnlyList<(CsType Type, bool Opt)> parameters, (CsType Type, bool Opt)? result, bool nullable = false,
        bool jsNull = true)
    {
        string Spell(IEnumerable<string> types)
        {
            var list = types.ToList();
            var name = result is null ? (list.Count == 0 ? "global::System.Action" : $"global::System.Action<{string.Join(", ", list)}>") : $"global::System.Func<{string.Join(", ", list)}>";
            return nullable ? name + "?" : name;
        }
        static string OptName((CsType Type, bool Opt) p, Func<CsType, string> name) => p.Opt ? $"global::Interoper.Opt<{name(p.Type)}>" : name(p.Type);
        var all = result is { } r ? parameters.Append(r).ToList() : parameters.ToList();
        return new(Spell(all.Select(p => OptName(p, t => t.Name))), JsonKind.Object, isValueType: false, null, null,
            (w, v, d) =>
            {
                var readers = parameters.Select(p => p.Opt ? $"{p.Type.Static}(ref r{Suffix(d)}) => global::Interoper.JsJson.ReadOpt(ref r{Suffix(d)}, {p.Type.Reader(d + 1)})" : p.Type.Reader(d));
                var writer = result is not { } x ? null
                    : x.Opt ? $"static (w{Suffix(d)}, v{Suffix(d)}) => global::Interoper.JsJson.WriteValue(w{Suffix(d)}, v{Suffix(d)}, {x.Type.Writer(d + 1)})" : x.Type.Writer(d);
                var id = $"scope.Callback({string.Join(", ", readers.Append(writer).OfType<string>().Prepend(v))})";
                return !nullable ? $"global::Interoper.JsJson.WriteCallback({w}, {id})"
                    : $"global::Interoper.JsJson.WriteCallback({w}, {v} is null ? null : {id}{(jsNull ? "" : ", undefined: true")})";
            })
        {
            IsCallback = true,
            Returns = result is not null,
            fullName = Spell(all.Select(p => OptName(p, t => t.FullName))),
            rebuild = change => Callback([.. parameters.Select(p => (change(p.Type), p.Opt))], result is { } x ? (change(x.Type), x.Opt) : null, nullable, jsNull),
            nullableCallback = isNull => Callback(parameters, result, nullable: true, jsNull: isNull),
        };
    }

    /// <summary>
    /// This callback as one that can be null (<c>Func&lt;…&gt;?</c>, R21), which JS gets as null when <paramref name="jsNull"/>
    /// (its type has null), and as undefined otherwise (an optional one).
    /// </summary>
    public CsType NullableCallback(bool jsNull) =>
        nullableCallback is { } make ? make(jsNull) : throw new InvalidOperationException($"`{Name}` isn't a callback");

    /// <summary>An array's element type.</summary>
    public CsType? Element { get; private init; }

    /// <summary>This type as code in namespace <paramref name="ns"/> names it: <c>global::</c>-qualified from another (R27).</summary>
    public CsType In(string ns) =>
        rebuild is not null ? rebuild(t => t.In(ns))
        : Namespace is null || Namespace == ns && args.All(a => a.In(ns).Name == a.Name) ? this
        : Qualified(named!(Generic(new TypeName(Namespace, baseName).In(ns), args.Select(a => a.In(ns).Name))));

    private CsType Qualified(CsType spelled)
    {
        spelled.fullName = FullName;
        return spelled;
    }

    /// <summary>
    /// This type, calling its static methods through <c>global::</c> where a member of the type's name hides it
    /// (<paramref name="hidden"/>): in a record with <c>Opt&lt;Cursor&gt; Cursor</c>, <c>Cursor.Read</c> is that property (CS0120).
    /// </summary>
    public CsType Unhidden(IReadOnlySet<string> hidden)
    {
        if (rebuild is not null)
        {
            return rebuild(t => t.Unhidden(hidden));
        }
        if (Namespace is null || !hidden.Contains(baseName))
        {
            return this;
        }
        if (args.Count > 0)
        {
            return Qualified(named!(Generic(new TypeName(Namespace, baseName).FullName, args.Select(a => a.Name))));
        }
        string? Qualify(string? method) => method?.StartsWith(Name + ".", StringComparison.Ordinal) == true ? $"global::{Namespace}.{method}" : method;
        // A scoped record's reader is a call, not a method group: Cursor.Read(ref r, scope).
        Func<string, int, string>? QualifyRead(Func<string, int, string>? f) => f is null ? null : (r, d) => Qualify(f(r, d))!;
        return new(Name, Json, IsValueType, Qualify(writeMethod), Qualify(readMethod), write, QualifyRead(read), IsUnion, Scoped, Fast, Names) { Namespace = Namespace, baseName = baseName, named = named, Wraps = Wraps };
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
