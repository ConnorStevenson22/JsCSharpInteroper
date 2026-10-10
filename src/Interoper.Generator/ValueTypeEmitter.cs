using System.Diagnostics;
using System.Text.Json;

namespace Interoper.Generator;

/// <summary>Enums (R9, numeric), extensible enums (R10) and union structs (R2, R19, R20).</summary>
internal static class ValueTypeEmitter
{
    public static string Emit(TypeMap types, string ns, DataType type) => type switch
    {
        EnumType { Extensible: true } e => Extensible(ns, e),
        EnumType { IsNumber: true } e => NumberEnum(ns, e),
        EnumType e => StringEnum(ns, e),
        _ => Union(types, ns, (UnionStruct)type),
    };

    /// <summary>R9: an enum, and a name map between its members and the JS strings.</summary>
    private static string StringEnum(string ns, EnumType e)
    {
        var w = CodeWriter.CSharp(ns, "System", "System.Text.Json");
        Enum(w, e, _ => "");
        w.Line();
        using (w.Block($"public static partial class {e.Name}Names"))
        {
            using (w.Block($"public static string ToJs({e.Name} value) => value switch", "};"))
            {
                foreach (var (name, value) in e.Members)
                {
                    w.Line($"{e.Name}.{name} => {Syntax.Literal(value.GetString()!)},");
                }
                w.Line("_ => throw new ArgumentOutOfRangeException(nameof(value)),");
            }
            w.Line();
            using (w.Block($"public static {e.Name} FromJs(string value) => value switch", "};"))
            {
                foreach (var (name, value) in e.Members)
                {
                    w.Line($"{Syntax.Literal(value.GetString()!)} => {e.Name}.{name},");
                }
                w.Line($"_ => throw new JsonException($\"{e.Name}: unexpected \\\"{{value}}\\\"\"),");
            }
            w.Line();
            w.Line($"public static void Write(Utf8JsonWriter writer, {e.Name} value) => writer.WriteStringValue(ToJs(value));");
            w.Line();
            w.Line($"public static {e.Name} Read(ref Utf8JsonReader reader) => FromJs(reader.GetString()!);");
        }
        return w.ToString();
    }

    /// <summary>A number-literal union: an enum with explicit values, written as numbers.</summary>
    private static string NumberEnum(string ns, EnumType e)
    {
        var w = CodeWriter.CSharp(ns);
        Enum(w, e, value => $" = {value.GetRawText()}");
        return w.ToString();
    }

    private static void Enum(CodeWriter w, EnumType e, Func<JsonElement, string> value)
    {
        using (w.Block($"public enum {e.Name}"))
        {
            foreach (var (name, v) in e.Members)
            {
                w.Line($"{name}{value(v)},");
            }
        }
    }

    /// <summary>R10: a string wrapper with the known values, which also takes any other.</summary>
    private static string Extensible(string ns, EnumType e)
    {
        var w = CodeWriter.CSharp(ns, "System.Text.Json");
        using (w.Block($"public readonly partial record struct {e.Name}(string Value)"))
        {
            foreach (var (name, value) in e.Members)
            {
                w.Line($"public static {e.Name} {name} {{ get; }} = new({Syntax.Literal(value.GetString()!)});");
            }
            w.Line();
            w.Line($"public static void Write(Utf8JsonWriter writer, {e.Name} value) => writer.WriteStringValue(value.Value);");
            w.Line();
            w.Line($"public static {e.Name} Read(ref Utf8JsonReader reader) => new(reader.GetString()!);");
            w.Line();
            w.Line("public override string ToString() => Value;");
        }
        return w.ToString();
    }

    /// <summary>
    /// R2: a non-boxing struct with a tag per case (0 undefined, 1 null), written by its tag (R20) and read by R19:
    /// the discriminant's value, or the JSON token, and for several object cases a required member only one has.
    /// </summary>
    private static string Union(TypeMap types, string ns, UnionStruct u)
    {
        var name = u.Name;
        var cases = u.Cases.Select((c, i) => (c.Type, Tag: i + 2, c.Declaration)).ToList();
        var w = CodeWriter.CSharp(ns, "System", "System.Diagnostics.CodeAnalysis", "System.Runtime.CompilerServices", "System.Text.Json", "Interoper");
        w.Line("[Union]");
        using (w.Block($"public readonly partial struct {name} : IJsUnion<{name}>, IEquatable<{name}>"))
        {
            w.Line("private readonly object? _ref;");
            w.Line($"private readonly byte _tag; // 0 = undefined, 1 = null, {string.Join(", ", cases.Select(c => $"{c.Tag} = {c.Type.Name}"))}");
            w.Line();
            w.Line($"private {name}(object? value, byte tag) => (_ref, _tag) = (value, tag);");
            foreach (var (type, tag, _) in cases)
            {
                w.Line($"public {name}({type.Name} value) : this(value, {tag}) {{ }}");
            }
            w.Line($"public {name}(Undefined? value) : this(null, value is null ? (byte)1 : (byte)0) {{ }}");
            w.Line();
            w.Line($"public static implicit operator {name}(Undefined? value) => new(value);");
            w.Line();
            w.Line("public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };");
            w.Line("public bool HasValue => _tag != 1;");
            w.Line("public bool IsUndefined => _tag == 0;");
            w.Line("public bool IsNull => _tag == 1;");
            w.Line();
            using (w.Block("public bool TryGetValue(out Undefined value)"))
            {
                w.Line("value = Undefined.Value;");
                w.Line("return _tag == 0;");
            }
            foreach (var (type, tag, _) in cases)
            {
                w.Line();
                using (w.Block($"public bool TryGetValue([MaybeNullWhen(false)] out {type.Name} value)"))
                {
                    w.Line($"value = _tag == {tag} ? ({type.Name})_ref! : null;");
                    w.Line($"return _tag == {tag};");
                }
            }
            w.Line();
            using (w.Block($"public static void WriteValue(Utf8JsonWriter writer, {name} value)"))
            using (w.Block("switch (value._tag)"))
            {
                foreach (var (type, tag, _) in cases)
                {
                    w.Line($"case {tag}: {type.WriteCall("writer", $"({type.Name})value._ref!", 0)}; break;");
                }
            }
            w.Line();
            ReadValue(w, types, u);
            w.Line();
            w.Line($"public bool Equals({name} other) => _tag == other._tag && Equals(_ref, other._ref);");
            w.Line($"public override bool Equals(object? obj) => obj is {name} other && Equals(other);");
            w.Line("public override int GetHashCode() => HashCode.Combine(_tag, _ref);");
            w.Line("public override string ToString() => _tag switch { 0 => \"undefined\", 1 => \"null\", _ => $\"{_ref}\" };");
        }
        return w.ToString();
    }

    /// <summary>R19: by the discriminant (step 2), or by the JSON token (step 4), objects last, told apart by a required member (step 3).</summary>
    private static void ReadValue(CodeWriter w, TypeMap types, UnionStruct u)
    {
        var header = $"public static {u.Name} ReadValue(ref Utf8JsonReader reader)";
        if (u.Discriminant is { } discriminant)
        {
            var cases = u.Cases.Select(c => (types.Constant(c.Declaration!, discriminant), c.Type.Name));
            RecordEmitter.DiscriminantSwitch(w, header, u.Name, discriminant, cases);
            return;
        }
        var objects = u.Cases.Where(c => c.Type.Json == JsonKind.Object).ToList();
        var others = u.Cases.Except(objects).ToList();
        using (w.Block($"{header} => reader.TokenType switch", "};"))
        {
            foreach (var c in others)
            {
                w.Line($"JsonTokenType.{(c.Type.Json == JsonKind.String ? "String" : "StartArray")} => {c.Type.ReadCall("reader", 0)},");
            }
            foreach (var c in objects)
            {
                var when = objects.Count == 1 ? "" : $" when JsJson.HasProperty(reader, {Syntax.Utf8(Distinct(types, u, c, objects))})";
                w.Line($"JsonTokenType.StartObject{when} => {c.Type.ReadCall("reader", 0)},");
            }
            w.Line($"var token => throw new JsonException($\"{u.Name}: unexpected {{token}}\"),");
        }
    }

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
