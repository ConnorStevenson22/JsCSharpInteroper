using System.Text.Json;
using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>Data records (R15, R20) and snapshot records (R16–R18), with their JSON readers and writers.</summary>
internal static class RecordEmitter
{
    /// <summary>
    /// A data record: plain JSON, its constants written in place (R20). One that holds an entity is read with a scope
    /// (R17); one that holds a DOM element is only written, so it has no reader (R13). A generic interface is a generic
    /// record whose type parameters are entities (mapping §2). Any other is an <c>IJsRecord</c>, with a System.Text.Json
    /// converter.
    /// </summary>
    public static string Data(TypeMap types, Declaration declaration)
    {
        var (ns, name) = types.Name(declaration);
        var parameters = declaration is InterfaceDeclaration i ? i.TypeParameters.Select(t => t.Name).ToList() : [];
        if (parameters.Count > 0)
        {
            name = $"{name}<{string.Join(", ", parameters)}>";
        }
        var fields = Unhidden(types.Fields(declaration), ns);
        var readable = !types.HoldsElement(declaration);
        var plain = readable && parameters.Count == 0 && !types.IsScoped(declaration);
        var w = CodeWriter.CSharp(ns);
        XmlDoc.Write(w, declaration.Doc);
        if (plain)
        {
            Converter(w, name);
        }
        using (w.Block($"public sealed partial record {name}{(plain ? $" : global::Interoper.IJsRecord<{name}>" : "")}{string.Concat(parameters.Select(t => $" where {t} : global::Interoper.JsObject"))}"))
        {
            Properties(w, fields, required: true);
            using (w.Block($"public static void Write(global::System.Text.Json.Utf8JsonWriter writer, {name} value)"))
            {
                w.Line("writer.WriteStartObject();");
                WriteConstants(w, fields);
                WriteFields(w, fields);
                w.Line("writer.WriteEndObject();");
            }
            if (readable)
            {
                w.Line();
                Read(w, name, "", fields, types.IsScoped(declaration));
            }
        }
        return w.ToString();
    }

    /// <summary>
    /// A class's snapshot record (R18), an <c>IJsRecord</c> with a System.Text.Json converter. A value-like one, or an
    /// entity's that takes a bag, is written as a value: <c>$c</c> and its constants (R16), and any other as an object of
    /// its members and constants.
    /// An abstract value-like class's is the closed base of its subclasses', read by their discriminant (R19 step 2).
    /// </summary>
    public static string Snapshot(TypeMap types, ClassDeclaration c, ICollection<Diagnostic> diagnostics)
    {
        var (ns, name) = types.Snapshot(c);
        var fields = Unhidden(types.Fields(c), ns);
        var b = types.SnapshotBase(c);
        var w = CodeWriter.CSharp(ns);
        Converter(w, name);
        using (w.Block($"public {(types.IsClosed(c) ? "closed" : "sealed")} partial record {name} : {(b is null ? "" : types.Snapshot(b).In(ns) + ", ")}global::Interoper.IJsRecord<{name}>"))
        {
            Properties(w, fields, required: false);
            if (types.IsClosed(c))
            {
                ClosedBase(w, types, c, ns, name, b is null ? "" : "new ");
            }
            else
            {
                Leaf(w, types, c, ns, name, b is null ? "" : "new ", fields);
            }
            if (types.HasSnapshotMembers(c))
            {
                w.Line();
                Members(w, types, c, name, fields, b is not null && types.HasSnapshotMembers(b) ? "new " : "", diagnostics);
            }
        }
        return w.ToString();
    }

    /// <summary>
    /// R18: each member as a <c>JsMembers</c>, for partial snapshots and watches. A closed base reads its subclasses'
    /// snapshots by their discriminant (R19 step 2), so each of its members names that too. The class is <c>Members</c>
    /// unless a member has that name (IOP2006).
    /// </summary>
    private static void Members(CodeWriter w, TypeMap types, ClassDeclaration c, string name, IReadOnlyList<Field> fields, string modifier,
        ICollection<Diagnostic> diagnostics)
    {
        var discriminant = types.IsClosed(c) ? ", " + Syntax.Literal(types.Discriminant(c)) : "";
        var members = fields.Where(f => f.Constant is null).ToList();
        var taken = members.Select(f => f.Name).ToHashSet();
        using (w.Block($"public {modifier}static class {Naming.Member(taken, "Members", $"{name}.Members", diagnostics)}"))
        {
            foreach (var f in members)
            {
                XmlDoc.Write(w, f.Doc);
                w.Line($"public static global::Interoper.JsMembers<{name}> {f.Name} => new({Syntax.Literal(f.JsName)}{discriminant});");
            }
        }
    }

    /// <summary>Write, WriteMembers, Read and Diff.</summary>
    private static void Leaf(CodeWriter w, TypeMap types, ClassDeclaration c, string ns, string name, string modifier, IReadOnlyList<Field> fields)
    {
        using (w.Block($"public static void Write(global::System.Text.Json.Utf8JsonWriter writer, {name} value)"))
        {
            w.Line("writer.WriteStartObject();");
            if (types.IsValueLike(c) || types.TakesBag(c))
            {
                w.Line($"writer.WriteNumber(\"$c\"u8, {types.ClassId(c, ns)});");
            }
            WriteConstants(w, fields);
            w.Line("WriteMembers(writer, value);");
            w.Line("writer.WriteEndObject();");
        }
        w.Line();
        using (w.Block($"public static void WriteMembers(global::System.Text.Json.Utf8JsonWriter writer, {name} value)"))
        {
            WriteFields(w, fields);
        }
        w.Line();
        Read(w, name, modifier, fields);
        w.Line();
        Diff(w, name, fields);
    }

    /// <summary>
    /// The patch from <c>before</c> to <c>after</c> (R18): each member <c>after</c> defines and that isn't the same
    /// (<c>JsDiff.Same</c>) in <c>before</c>. Every snapshot member is optional, so undefined is its default.
    /// </summary>
    private static void Diff(CodeWriter w, string name, IReadOnlyList<Field> fields)
    {
        var header = $"public static {name} Diff({name} before, {name} after) => new()";
        var members = fields.Where(f => f.Constant is null).ToList();
        if (members.Count == 0)
        {
            w.Line($"{header};");
            return;
        }
        using (w.Block(header, "};"))
        {
            foreach (var f in members)
            {
                w.Line($"{f.Name} = after.{f.Name}.IsUndefined || global::Interoper.JsDiff.Same(before.{f.Name}, after.{f.Name}) ? default : after.{f.Name},");
            }
        }
    }

    /// <summary>Write and WriteMembers by the subclass, Read by the discriminant.</summary>
    private static void ClosedBase(CodeWriter w, TypeMap types, ClassDeclaration c, string ns, string name, string modifier)
    {
        var subclasses = types.ConcreteSubclasses(c);
        foreach (var method in new[] { "Write", "WriteMembers" })
        {
            using (w.Block($"public static void {method}(global::System.Text.Json.Utf8JsonWriter writer, {name} value)"))
            using (w.Block("switch (value)"))
            {
                foreach (var sub in subclasses)
                {
                    var (type, local) = (types.Snapshot(sub).In(ns), Syntax.Local(types.Name(sub).Name, "writer", "value"));
                    w.Line($"case {type} {local}: {type}.{method}(writer, {local}); break;");
                }
            }
            w.Line();
        }
        var discriminant = types.Discriminant(c);
        var cases = subclasses.Select(sub => (types.Constant(sub, discriminant), types.Snapshot(sub).In(ns)));
        DiscriminantSwitch(w, $"public static {modifier}{name} Read(ref global::System.Text.Json.Utf8JsonReader reader)", name, discriminant, cases);
    }

    /// <summary>
    /// R19 step 2: a switch on the discriminant's value. <paramref name="handle"/> is what a handle without one throws,
    /// for a union that dropped a case outside the selection (R2).
    /// </summary>
    public static void DiscriminantSwitch(CodeWriter w, string header, string type, string discriminant, IEnumerable<(string Value, string Type)> cases,
        string? handle = null)
    {
        var local = Syntax.Local(Naming.Pascal(discriminant), "reader");
        using (w.Block($"{header} => global::Interoper.JsJson.PeekString(reader, {Syntax.Utf8(discriminant)}) switch", "};"))
        {
            if (handle is not null)
            {
                w.Line($"null when global::Interoper.JsJson.HasProperty(reader, \"$h\"u8) => {handle},");
            }
            foreach (var (value, read) in cases)
            {
                w.Line($"{Syntax.Literal(value)} => {read}.Read(ref reader),");
            }
            w.Line($"var {local} => throw new global::System.Text.Json.JsonException($\"{type}: unexpected {discriminant} {{{local}}}\"),");
        }
    }

    /// <summary>The attribute that serializes the record with System.Text.Json in its wire format.</summary>
    private static void Converter(CodeWriter w, string name) =>
        w.Line($"[global::System.Text.Json.Serialization.JsonConverter(typeof(global::Interoper.JsRecordConverter<{name}>))]");

    /// <summary>
    /// The fields' types as code in namespace <paramref name="ns"/> names them (R27). A property hides a type of its name
    /// unless it has that type: <c>Opt&lt;Cursor&gt; Cursor</c> (CS0120).
    /// </summary>
    private static List<Field> Unhidden(IReadOnlyList<Field> fields, string ns)
    {
        var named = fields.Select(f => f with { Type = f.Type?.In(ns) }).ToList();
        var hidden = named.Where(f => f.Type is not null && f.TypeName != f.Name).Select(f => f.Name).ToHashSet();
        return [.. named.Select(f => f with { Type = f.Type?.Unhidden(hidden) })];
    }

    private static void Properties(CodeWriter w, IReadOnlyList<Field> fields, bool required)
    {
        var own = fields.Where(f => f.Constant is null && !f.Inherited).ToList();
        foreach (var f in own)
        {
            XmlDoc.Write(w, f.Doc);
            w.Line($"public {(f.Hides ? "new " : "")}{(required && f.Required ? "required " : "")}{f.TypeName} {f.Name} {{ get; init; }}");
        }
        if (own.Count > 0)
        {
            w.Line();
        }
    }

    /// <summary>The constants, always written (R20).</summary>
    private static void WriteConstants(CodeWriter w, IReadOnlyList<Field> fields)
    {
        foreach (var f in fields.Where(f => f.Constant is not null))
        {
            var value = f.Constant!.Value;
            w.Line(value.ValueKind switch
            {
                JsonValueKind.String => $"writer.WriteString({Syntax.Utf8(f.JsName)}, {Syntax.Utf8(value.GetString()!)});",
                JsonValueKind.Number => $"writer.WriteNumber({Syntax.Utf8(f.JsName)}, {value.GetRawText()});",
                _ => $"writer.WriteBoolean({Syntax.Utf8(f.JsName)}, {value.GetRawText()});",
            });
        }
    }

    /// <summary>The other members, undefined ones left out (R2, R3).</summary>
    private static void WriteFields(CodeWriter w, IReadOnlyList<Field> fields)
    {
        foreach (var f in fields.Where(f => f.Constant is null))
        {
            var name = Syntax.Utf8(f.JsName);
            if (f.Type!.IsUnion)
            {
                w.Line($"global::Interoper.JsJson.WriteProperty(writer, {name}, value.{f.Name});");
            }
            else if (f.Opt)
            {
                w.Line($"global::Interoper.JsJson.WriteProperty(writer, {name}, value.{f.Name}, {f.Type.Writer(0)});");
            }
            else
            {
                w.Line($"writer.WritePropertyName({name});");
                w.Line($"{f.Type.WriteCall("writer", $"value.{f.Name}", 0)};");
            }
        }
    }

    /// <summary>
    /// Reads the members it knows and skips the rest, constants and members only written (R2, R17) included. A
    /// <paramref name="scoped"/> record reads its entities with the caller's scope, which owns them (R17).
    /// </summary>
    private static void Read(CodeWriter w, string name, string modifier, IReadOnlyList<Field> fields, bool scoped = false)
    {
        var read = fields.Where(f => f.Constant is null && !f.WriteOnly).Select(f => (Field: f, Local: Syntax.Local(f.Name, "reader", "property", "scope"))).ToList();
        var unread = fields.Where(f => f.WriteOnly && f.Required).Select(f => $"{f.Name} = default").ToList(); // undefined, though `required`
        using (w.Block($"public static {modifier}{name} Read(ref global::System.Text.Json.Utf8JsonReader reader{(scoped ? ", global::Interoper.JsScope scope" : "")})"))
        {
            if (read.Count == 0)
            {
                w.Line("reader.Skip();");
                w.Line(unread.Count == 0 ? $"return new {name}();" : $"return new {name} {{ {string.Join(", ", unread)} }};");
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
            using (w.Block("while (reader.Read() && reader.TokenType == global::System.Text.Json.JsonTokenType.PropertyName)"))
            {
                w.Line("var property = reader.GetString();");
                w.Line("reader.Read();");
                using (w.Block("switch (property)"))
                {
                    foreach (var (f, local) in read)
                    {
                        var value = f.Opt ? $"global::Interoper.JsJson.ReadOpt(ref reader, {f.Type!.Reader(0)})" : f.Type!.ReadCall("reader", 0);
                        w.Line($"case {Syntax.Literal(f.JsName)}: {local} = {value}; break;");
                    }
                    w.Line("default: reader.Skip(); break;");
                }
            }
            w.Line($"return new {name} {{ {string.Join(", ", read.Select(x => $"{x.Field.Name} = {x.Local}").Concat(unread))} }};");
        }
    }

    /// <summary>Undefined for an optional member; for a required one, a placeholder the reader overwrites.</summary>
    private static string Initial(Field f) =>
        f.Opt || f.Type!.IsValueType ? "default" : f.Type.Json == JsonKind.Array ? "[]" : "null!";
}
