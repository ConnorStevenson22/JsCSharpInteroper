using System.Text.Json;
using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>Data records (R15, R20) and snapshot records (R16–R18), with their JSON readers and writers.</summary>
internal static class RecordEmitter
{
    /// <summary>A data record: plain JSON, its constants written in place (R20).</summary>
    public static string Data(TypeMap types, string ns, Declaration declaration)
    {
        var name = Naming.Type(declaration.Name);
        var fields = types.Fields(declaration);
        var w = CodeWriter.CSharp(ns, "System.Text.Json", "Interoper");
        using (w.Block($"public sealed partial record {name}"))
        {
            Properties(w, fields, required: true);
            using (w.Block($"public static void Write(Utf8JsonWriter writer, {name} value)"))
            {
                w.Line("writer.WriteStartObject();");
                WriteFields(w, fields, constants: true);
                w.Line("writer.WriteEndObject();");
            }
            w.Line();
            Read(w, name, "", fields);
        }
        return w.ToString();
    }

    /// <summary>
    /// A class's snapshot record (R18). A value-like one is also written as a value: <c>$c</c> and its constants (R16).
    /// An abstract value-like class's is the closed base of its subclasses', read by their discriminant (R19 step 2).
    /// </summary>
    public static string Snapshot(TypeMap types, string ns, ClassDeclaration c)
    {
        var name = TypeMap.SnapshotName(c);
        var fields = types.Fields(c);
        var b = types.SnapshotBase(c);
        var w = CodeWriter.CSharp(ns, "System.Text.Json", "Interoper");
        using (w.Block($"public {(types.IsClosed(c) ? "closed" : "sealed")} partial record {name}{(b is null ? "" : " : " + TypeMap.SnapshotName(b))}"))
        {
            Properties(w, fields, required: false);
            if (types.IsClosed(c))
            {
                ClosedBase(w, types, c, name, b is null ? "" : "new ");
            }
            else
            {
                Leaf(w, types, c, name, b is null ? "" : "new ", fields);
            }
        }
        return w.ToString();
    }

    /// <summary>WriteMembers and Read, and for a value-like class Write.</summary>
    private static void Leaf(CodeWriter w, TypeMap types, ClassDeclaration c, string name, string modifier, IReadOnlyList<Field> fields)
    {
        if (types.IsValueLike(c))
        {
            using (w.Block($"public static void Write(Utf8JsonWriter writer, {name} value)"))
            {
                w.Line("writer.WriteStartObject();");
                w.Line($"writer.WriteNumber(\"$c\"u8, {types.Registry}.{Naming.Type(c.Name)}Id);");
                WriteConstants(w, fields);
                w.Line("WriteMembers(writer, value);");
                w.Line("writer.WriteEndObject();");
            }
            w.Line();
        }
        using (w.Block($"public static void WriteMembers(Utf8JsonWriter writer, {name} value)"))
        {
            WriteFields(w, fields, constants: false);
        }
        w.Line();
        Read(w, name, modifier, fields);
    }

    /// <summary>Write and WriteMembers by the subclass, Read by the discriminant.</summary>
    private static void ClosedBase(CodeWriter w, TypeMap types, ClassDeclaration c, string name, string modifier)
    {
        var subclasses = types.ConcreteSubclasses(c).Select(TypeMap.SnapshotName).ToList();
        foreach (var method in new[] { "Write", "WriteMembers" })
        {
            using (w.Block($"public static void {method}(Utf8JsonWriter writer, {name} value)"))
            using (w.Block("switch (value)"))
            {
                foreach (var sub in subclasses)
                {
                    var local = Syntax.Local(sub[..^"Snapshot".Length], "writer", "value");
                    w.Line($"case {sub} {local}: {sub}.{method}(writer, {local}); break;");
                }
            }
            w.Line();
        }
        var discriminant = types.Discriminant(c);
        var cases = types.ConcreteSubclasses(c).Select(sub => (types.Fields(sub).Single(f => f.JsName == discriminant).Constant!.Value.GetString()!, TypeMap.SnapshotName(sub)));
        DiscriminantSwitch(w, $"public static {modifier}{name} Read(ref Utf8JsonReader reader)", name, discriminant, cases);
    }

    /// <summary>R19 step 2: a switch on the discriminant's value.</summary>
    public static void DiscriminantSwitch(CodeWriter w, string header, string type, string discriminant, IEnumerable<(string Value, string Type)> cases)
    {
        var local = Syntax.Local(Naming.Pascal(discriminant), "reader");
        using (w.Block($"{header} => JsJson.PeekString(reader, {Syntax.Utf8(discriminant)}) switch", "};"))
        {
            foreach (var (value, read) in cases)
            {
                w.Line($"{Syntax.Literal(value)} => {read}.Read(ref reader),");
            }
            w.Line($"var {local} => throw new JsonException($\"{type}: unexpected {discriminant} {{{local}}}\"),");
        }
    }

    private static void Properties(CodeWriter w, IReadOnlyList<Field> fields, bool required)
    {
        var own = fields.Where(f => f.Constant is null && !f.Inherited).ToList();
        foreach (var f in own)
        {
            w.Line($"public {(required && f.Required ? "required " : "")}{f.TypeName} {f.Name} {{ get; init; }}");
        }
        if (own.Count > 0)
        {
            w.Line();
        }
    }

    private static void WriteConstants(CodeWriter w, IReadOnlyList<Field> fields)
    {
        foreach (var f in fields.Where(f => f.Constant is not null))
        {
            WriteConstant(w, f);
        }
    }

    private static void WriteConstant(CodeWriter w, Field f)
    {
        var value = f.Constant!.Value;
        w.Line(value.ValueKind switch
        {
            JsonValueKind.String => $"writer.WriteString({Syntax.Utf8(f.JsName)}, {Syntax.Utf8(value.GetString()!)});",
            JsonValueKind.Number => $"writer.WriteNumber({Syntax.Utf8(f.JsName)}, {value.GetRawText()});",
            _ => $"writer.WriteBoolean({Syntax.Utf8(f.JsName)}, {value.GetRawText()});",
        });
    }

    /// <summary>The members, undefined ones left out (R2, R3).</summary>
    private static void WriteFields(CodeWriter w, IReadOnlyList<Field> fields, bool constants)
    {
        foreach (var f in fields)
        {
            var name = Syntax.Utf8(f.JsName);
            if (f.Constant is not null)
            {
                if (constants)
                {
                    WriteConstant(w, f);
                }
            }
            else if (f.Type!.IsUnion)
            {
                w.Line($"JsJson.WriteProperty(writer, {name}, value.{f.Name});");
            }
            else if (f.Opt)
            {
                w.Line($"JsJson.WriteProperty(writer, {name}, value.{f.Name}, {f.Type.Writer(0)});");
            }
            else
            {
                w.Line($"writer.WritePropertyName({name});");
                w.Line($"{f.Type.WriteCall("writer", $"value.{f.Name}", 0)};");
            }
        }
    }

    /// <summary>Reads the members it knows and skips the rest, constants included.</summary>
    private static void Read(CodeWriter w, string name, string modifier, IReadOnlyList<Field> fields)
    {
        var read = fields.Where(f => f.Constant is null).Select(f => (Field: f, Local: Syntax.Local(f.Name, "reader", "property"))).ToList();
        using (w.Block($"public static {modifier}{name} Read(ref Utf8JsonReader reader)"))
        {
            if (read.Count == 0)
            {
                w.Line("reader.Skip();");
                w.Line($"return new {name}();");
                return;
            }
            // One declaration per run of locals of one type: double minX = default, minY = default;
            for (var i = 0; i < read.Count;)
            {
                var type = read[i].Field.TypeName;
                var run = read.Skip(i).TakeWhile(x => x.Field.TypeName == type).ToList();
                w.Line($"{type} {string.Join(", ", run.Select(x => $"{x.Local} = {Initial(x.Field)}"))};");
                i += run.Count;
            }
            using (w.Block("while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)"))
            {
                w.Line("var property = reader.GetString();");
                w.Line("reader.Read();");
                using (w.Block("switch (property)"))
                {
                    foreach (var (f, local) in read)
                    {
                        var value = f.Opt ? $"JsJson.ReadOpt(ref reader, {f.Type!.Reader(0)})" : f.Type!.ReadCall("reader", 0);
                        w.Line($"case {Syntax.Literal(f.JsName)}: {local} = {value}; break;");
                    }
                    w.Line("default: reader.Skip(); break;");
                }
            }
            w.Line($"return new {name} {{ {string.Join(", ", read.Select(x => $"{x.Field.Name} = {x.Local}"))} }};");
        }
    }

    /// <summary>Undefined for an optional member; for a required one, a placeholder the reader overwrites.</summary>
    private static string Initial(Field f) =>
        f.Opt || f.Type!.IsValueType ? "default" : f.Type.Json == JsonKind.Array ? "[]" : "null!";
}
