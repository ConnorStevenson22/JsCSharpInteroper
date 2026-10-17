using System.Diagnostics;
using System.Text.Json;
using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>Enums (R9, numeric), extensible enums (R10) and union structs (R2, R19, R20).</summary>
internal static class ValueTypeEmitter
{
    /// <summary>The type, in its namespace (R27), with the docs of the <paramref name="declaration"/> it is, if any (R29).</summary>
    public static string Emit(TypeMap types, DataType type, Declaration? declaration = null)
    {
        var w = CodeWriter.CSharp(type.Type.Namespace);
        XmlDoc.Write(w, declaration?.Doc);
        // A TS enum's members have their own docs; a literal union's have none, nor has an extensible enum (R10), which
        // only a literal union is.
        var docs = declaration is EnumDeclaration e ? e.Members.Select(m => m.Doc).ToList() : null;
        switch (type)
        {
            case EnumType { Extensible: true } extensible:
                Extensible(w, extensible);
                break;
            case EnumType { IsNumber: true } number:
                Enum(w, number, docs, value => $" = {value.GetRawText()}");
                break;
            case EnumType strings:
                StringEnum(w, strings, docs);
                break;
            default:
                Union(w, types, (UnionStruct)type);
                break;
        }
        return w.ToString();
    }

    /// <summary>R9: an enum, and a name map between its members and the JS strings.</summary>
    private static void StringEnum(CodeWriter w, EnumType e, List<Doc?>? docs)
    {
        Enum(w, e, docs, _ => "");
        w.Line();
        using (w.Block($"public static partial class {e.Name}Names"))
        {
            using (w.Block($"public static string ToJs({e.Name} value) => value switch", "};"))
            {
                foreach (var (name, value) in e.Members)
                {
                    w.Line($"{e.Name}.{name} => {Syntax.Literal(value.GetString()!)},");
                }
                w.Line("_ => throw new global::System.ArgumentOutOfRangeException(nameof(value)),");
            }
            w.Line();
            using (w.Block($"public static {e.Name} FromJs(string value) => value switch", "};"))
            {
                foreach (var (name, value) in e.Members)
                {
                    w.Line($"{Syntax.Literal(value.GetString()!)} => {e.Name}.{name},");
                }
                w.Line($"_ => throw new global::System.Text.Json.JsonException($\"{e.Name}: unexpected \\\"{{value}}\\\"\"),");
            }
            w.Line();
            w.Line($"public static void Write(global::System.Text.Json.Utf8JsonWriter writer, {e.Name} value) => writer.WriteStringValue(ToJs(value));");
            w.Line();
            w.Line($"public static {e.Name} Read(ref global::System.Text.Json.Utf8JsonReader reader) => FromJs(global::Interoper.JsJson.ReadString(ref reader));");
        }
    }

    /// <summary>The enum; a number-literal union's has explicit values (<paramref name="value"/>), written as numbers.</summary>
    private static void Enum(CodeWriter w, EnumType e, List<Doc?>? docs, Func<JsonElement, string> value)
    {
        using (w.Block($"public enum {e.Name}"))
        {
            foreach (var (i, (name, v)) in e.Members.Index())
            {
                XmlDoc.Write(w, docs?[i]);
                w.Line($"{name}{value(v)},");
            }
        }
    }

    /// <summary>R10: a string wrapper with the known values, which also takes any other.</summary>
    private static void Extensible(CodeWriter w, EnumType e)
    {
        using (w.Block($"public readonly partial record struct {e.Name}(string Value)"))
        {
            foreach (var (name, value) in e.Members)
            {
                w.Line($"public static {e.Name} {name} {{ get; }} = new({Syntax.Literal(value.GetString()!)});");
            }
            w.Line();
            w.Line($"public static void Write(global::System.Text.Json.Utf8JsonWriter writer, {e.Name} value) => writer.WriteStringValue(value.Value);");
            w.Line();
            w.Line($"public static {e.Name} Read(ref global::System.Text.Json.Utf8JsonReader reader) => new(global::Interoper.JsJson.ReadString(ref reader));");
            w.Line();
            w.Line("public override string ToString() => Value;");
        }
    }

    /// <summary>
    /// R2: a non-boxing struct with a tag per case (0 undefined, 1 null), written by its tag (R20) and read by R19:
    /// the discriminant's value, or the JSON token, and for several object cases a required member only one has.
    /// </summary>
    private static void Union(CodeWriter w, TypeMap types, UnionStruct u)
    {
        var (ns, name) = u.Type;
        u = u with { Cases = [.. u.Cases.Select(c => c with { Type = c.Type.In(ns) })] }; // R27
        var cases = u.Cases.Select((c, i) => (c.Type, Tag: i + 2, c.Declaration)).ToList();
        w.Line("[global::System.Runtime.CompilerServices.Union]");
        using (w.Block($"public readonly partial struct {name} : global::Interoper.IJsUnion<{name}>, global::System.IEquatable<{name}>"))
        {
            w.Line("private readonly object? _ref;");
            w.Line($"private readonly byte _tag; // 0 = undefined, 1 = null, {string.Join(", ", cases.Select(c => $"{c.Tag} = {c.Type.Name}"))}");
            w.Line();
            w.Line($"private {name}(object? value, byte tag) => (_ref, _tag) = (value, tag);");
            foreach (var (type, tag, _) in cases)
            {
                w.Line($"public {name}({type.Name} value) : this(value{(IsElement(type) ? ".Id" : "")}, {tag}) {{ }}");
            }
            w.Line($"public {name}(global::Interoper.Undefined? value) : this(null, value is null ? (byte)1 : (byte)0) {{ }}");
            w.Line();
            w.Line($"public static implicit operator {name}(global::Interoper.Undefined? value) => new(value);");
            w.Line();
            var element = cases.Where(c => IsElement(c.Type)).Select(c => $"{c.Tag} => {Load(c.Type)}, ").FirstOrDefault();
            w.Line($"public object? Value => _tag switch {{ 0 => global::Interoper.Undefined.Value, 1 => null, {element}_ => _ref }};");
            w.Line("public bool HasValue => _tag != 1;");
            w.Line("public bool IsUndefined => _tag == 0;");
            w.Line("public bool IsNull => _tag == 1;");
            w.Line();
            using (w.Block("public bool TryGetValue(out global::Interoper.Undefined value)"))
            {
                w.Line("value = global::Interoper.Undefined.Value;");
                w.Line("return _tag == 0;");
            }
            foreach (var (type, tag, _) in cases)
            {
                w.Line();
                using (w.Block($"public bool TryGetValue([global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {type.Name} value)"))
                {
                    w.Line($"value = _tag == {tag} ? {Load(type)} : {(IsElement(type) ? "default" : "null")};");
                    w.Line($"return _tag == {tag};");
                }
            }
            w.Line();
            using (w.Block($"public static void WriteValue(global::System.Text.Json.Utf8JsonWriter writer, {name} value)"))
            using (w.Block("switch (value._tag)"))
            {
                foreach (var (type, tag, _) in cases)
                {
                    w.Line($"case {tag}: {type.WriteCall("writer", Load(type, "value._ref"), 0)}; break;");
                }
            }
            w.Line();
            ReadValue(w, types, u);
            w.Line();
            w.Line($"public bool Equals({name} other) => _tag == other._tag && Equals(_ref, other._ref);");
            w.Line($"public override bool Equals(object? obj) => obj is {name} other && Equals(other);");
            w.Line("public override int GetHashCode() => global::System.HashCode.Combine(_tag, _ref);");
            w.Line("public override string ToString() => _tag switch { 0 => \"undefined\", 1 => \"null\", _ => $\"{_ref}\" };");
        }
    }

    /// <summary>
    /// R13: an element case, an <c>ElementReference</c>, is a value type, so the struct keeps its id, which is all
    /// <c>JsElement.Write</c> sends, and doesn't box it (R2).
    /// </summary>
    private static bool IsElement(CsType type) => type.FullName == CsType.DomElement.FullName;

    /// <summary>A case's value from <paramref name="field"/>: cast, or for an element case made from its id.</summary>
    private static string Load(CsType type, string field = "_ref") =>
        IsElement(type) ? $"new {type.Name}((string){field}!)" : $"({type.Name}){field}!";

    /// <summary>
    /// R19: by the discriminant (step 2), or by the JSON token (step 4), objects last, told apart by a required member
    /// (step 3). A union that is only written can't be read, and one that dropped a case fails on it (R2).
    /// </summary>
    private static void ReadValue(CodeWriter w, TypeMap types, UnionStruct u)
    {
        var header = $"public static {u.Name} ReadValue(ref global::System.Text.Json.Utf8JsonReader reader)";
        if (u.WrittenOnly)
        {
            // R2: it has an entity case, or dropped a case outside the selection and can't tell the others apart, so
            // it's only a parameter's.
            w.Line($"{header} => throw new global::System.Text.Json.JsonException(\"{u.Name} is only written (R2)\");");
            return;
        }
        if (u.Discriminant is { } discriminant)
        {
            var cases = u.Cases.Select(c => (types.Constant(c.Declaration!, discriminant), c.Type.Name));
            RecordEmitter.DiscriminantSwitch(w, header, u.Name, discriminant, cases, u.Dropped ? Outside(u) : null);
            return;
        }
        var objects = u.Cases.Where(c => c.Type.Json == JsonKind.Object).ToList();
        var others = u.Cases.Except(objects).ToList();
        using (w.Block($"{header} => reader.TokenType switch", "};"))
        {
            if (u.Dropped && objects.Count > 0)
            {
                // R2: JS sends an instance of a class outside the selection, a dropped case, as a handle; without an
                // object case, the last arm throws for it.
                w.Line($"global::System.Text.Json.JsonTokenType.StartObject when global::Interoper.JsJson.HasProperty(reader, \"$h\"u8) => {Outside(u)},");
            }
            foreach (var c in others)
            {
                w.Line($"global::System.Text.Json.JsonTokenType.{(c.Type.Json == JsonKind.String ? "String" : "StartArray")} => {c.Type.ReadCall("reader", 0)},");
            }
            foreach (var c in objects)
            {
                var when = objects.Count == 1 ? "" : $" when global::Interoper.JsJson.HasProperty(reader, {Syntax.Utf8(Distinct(types, u, c, objects))})";
                w.Line($"global::System.Text.Json.JsonTokenType.StartObject{when} => {c.Type.ReadCall("reader", 0)},");
            }
            w.Line($"var token => throw new global::System.Text.Json.JsonException($\"{u.Name}: unexpected {{token}}\"),");
        }
    }

    /// <summary>R2: what a reader throws for a handle, a dropped case outside the selection.</summary>
    private static string Outside(UnionStruct u) => $"throw new global::System.Text.Json.JsonException(\"{u.Name}: a case outside the selection (R2)\")";

    /// <summary>
    /// R19 step 3: the first required member of a case's wire form that no other object case has. Selection skips a
    /// union without one, and one whose other cases share a JSON token (IOP2003).
    /// </summary>
    private static string Distinct(TypeMap types, UnionStruct u, UnionCase c, List<UnionCase> objects)
    {
        var others = objects.Where(o => o != c).SelectMany(o => types.WireMembers(o.Declaration!).Select(m => m.Name)).ToHashSet();
        return types.WireMembers(c.Declaration!).Where(m => m.Required && !others.Contains(m.Name)).Select(m => m.Name).FirstOrDefault()
            ?? throw new UnreachableException($"{u.Name}: selection skips an ambiguous union (IOP2003).");
    }
}
