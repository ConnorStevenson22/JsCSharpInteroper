using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// Live classes and module functions (mapping §5), shaped by the mode: sync members call the realm's sync seam, async
/// ones return a <c>Task</c> with the <c>Async</c> suffix; a Promise is a <c>Task</c> in both (R8).
/// </summary>
internal sealed class LiveEmitter
{
    private const int LineLength = 120;

    private readonly TypeMap types;
    private readonly Profile profile;
    private readonly bool async;
    private readonly ICollection<Diagnostic> diagnostics;
    private readonly CodeWriter w = new();
    private readonly HashSet<string> taken = [];
    private bool usesSystem, usesTask, first = true;

    private LiveEmitter(TypeMap types, Profile profile, Api api, ICollection<Diagnostic> diagnostics)
    {
        this.types = types;
        this.profile = profile;
        async = api == Api.Async;
        usesTask = async;
        this.diagnostics = diagnostics;
    }

    /// <summary>A value as a member, parameter or result has it: <c>Opt&lt;T&gt;</c> when it can be undefined or null (R3).</summary>
    private sealed record Value(CsType Type, bool Opt)
    {
        public string Name => Opt ? $"Opt<{Type.Name}>" : Type.Name;

        public string Reader => Opt ? $"{Type.Static}(ref r) => JsJson.ReadOpt(ref r, {Type.Reader(1)})" : Type.Reader(0);

        public string Write(string value) => Opt ? $"JsJson.WriteValue(w, {value}, {Type.Writer(1)})" : Type.WriteCall("w", value, 1);

        /// <summary>The realm reads a <c>T?</c>; a non-null reference type needs a <c>!</c>.</summary>
        public bool MaybeNull => !Opt && !Type.IsValueType;
    }

    /// <summary>
    /// A live class (§5): its properties, methods, events (R24) and watches, <c>Create</c> unless it's abstract (R18), and
    /// <c>Snapshot</c>/<c>Apply</c>. An abstract value-like class is closed (R4).
    /// </summary>
    public static string Class(TypeMap types, Profile profile, GeneratorOptions options, ICollection<Diagnostic> diagnostics, Selected selected)
    {
        var e = new LiveEmitter(types, profile, options.Api, diagnostics);
        e.ClassBody((ClassDeclaration)selected.Declaration, selected.Watches);
        return e.File(options.Namespace);
    }

    /// <summary>A module's functions, as a static class named after its file (§5).</summary>
    public static string Module(TypeMap types, Profile profile, GeneratorOptions options, ICollection<Diagnostic> diagnostics,
        string name, string specifier, IEnumerable<FunctionDeclaration> functions)
    {
        var e = new LiveEmitter(types, profile, options.Api, diagnostics);
        e.ModuleBody(name, specifier, functions);
        return e.File(options.Namespace);
    }

    /// <summary>The name of a module's static class: its file name (<c>measure.js</c> → <c>Measure</c>).</summary>
    public static string ModuleClass(string specifier) => Naming.Type(Path.GetFileNameWithoutExtension(specifier));

    private string File(string ns)
    {
        var usings = new[] { usesSystem ? "System" : null, usesTask ? "System.Threading.Tasks" : null, "Interoper" };
        return CodeWriter.CSharp(ns, [.. usings.OfType<string>()]) + w.ToString();
    }

    private void ClassBody(ClassDeclaration c, IReadOnlyList<PropertyMember> watches)
    {
        var name = Naming.Type(c.Name);
        var snapshot = TypeMap.SnapshotName(c);
        var b = types.LiveBase(c);
        var modifier = types.IsClosed(c) ? "closed " : c.Abstract ? "abstract " : types.HasSubclasses(c) ? "" : "sealed ";
        taken.UnionWith([name, "Realm", "Handle", "Write", Mode("Snapshot"), Mode("Apply")]);
        using (w.Block($"public {modifier}partial class {name} : {(b is null ? "JsObject" : Naming.Type(b.Name))}"))
        {
            Separate();
            w.Line($"internal {name}(JsRealm realm, int handle) : base(realm, handle) {{ }}");
            if (!c.Abstract)
            {
                var (type, body) = Run("scope.Realm", $"JsOp.Create({types.Registry}.{name}Id, w => {snapshot}.WriteMembers(w, props))",
                    new(CsType.Live(name), false), async);
                Expression($"public static {type} {Claim(Mode("Create"), c.Name)}(JsScope scope, {snapshot} props)", body);
            }
            foreach (var p in c.Members.OfType<PropertyMember>().Where(p => p.Type is not LiteralType)) // R20: a constant is no property
            {
                Property(name, p);
            }
            foreach (var m in c.Members.OfType<MethodMember>())
            {
                Method(name, m);
            }
            foreach (var e in c.Events)
            {
                Subscription(name, "On" + Naming.Pascal(e.Name), profile.Events?.Subscribe, e.Name, e.Type, $"{name}.on:{e.Name}");
            }
            foreach (var p in watches)
            {
                Subscription(name, "Watch" + Naming.Pascal(p.Name), profile.Watch, p.Name, p.Type, $"{name}.watch:{p.Name}");
            }
            var hide = b is null ? "" : " new";
            var (snapshotType, read) = Run("Realm", "JsOp.Snapshot(Handle)", new(CsType.Record(snapshot), false), async);
            Expression($"public{hide} {snapshotType} {Mode("Snapshot")}()", read);
            var (applyType, apply) = Run("Realm", $"JsOp.Apply(Handle, w => {snapshot}.WriteMembers(w, patch))", null, async);
            Expression($"public {applyType} {Mode("Apply")}({snapshot} patch)", apply);
        }
    }

    /// <summary>
    /// §5: a property in sync mode, getters and setters in async mode. A setter whose type differs from the getter's is
    /// <c>SetX</c>, and an entity is read with the caller's scope (<c>GetX(scope)</c>). Non-null primitives and string
    /// enums use the realm's fast paths (design §3.4).
    /// </summary>
    private void Property(string owner, PropertyMember p)
    {
        var get = Slot(p.Type, Naming.OwnedType(owner, p.Name), owner, p.Optional);
        var set = p.Readonly ? null : p.WriteType is { } writeType ? Slot(writeType, Naming.OwnedType(owner, p.Name, input: true), owner, p.Optional) : get;
        var (js, x, subject) = (Syntax.Literal(p.Name), Naming.Pascal(p.Name), $"{owner}.{p.Name}");
        var (getType, getter) = Get(get, js, ByValue(p.Type));
        var (setType, setter) = set is null ? default : Set(set, js);
        if (async || get.Type.Scoped)
        {
            var scope = get.Type.Scoped ? "JsScope scope" : "";
            Expression($"public {getType} {Claim(Mode("Get" + x), subject)}({scope})", getter);
            if (set is not null)
            {
                Expression($"public {setType} {Claim(Mode("Set" + x), subject)}({set.Name} value)", setter);
            }
        }
        else if (set is null || set != get)
        {
            Expression($"public {getType} {Claim(x, subject)}", getter);
            if (set is not null)
            {
                Expression($"public void {Claim("Set" + x, subject)}({set.Name} value)", setter);
            }
        }
        else
        {
            Separate();
            using (w.Block($"public {getType} {Claim(x, subject)}"))
            {
                w.Line($"get => {getter};");
                w.Line($"set => {setter};");
            }
        }
    }

    private (string Type, string Body) Get(Value value, string js, string byValue)
    {
        if (value.Opt || value.Type.Fast is not { } fast)
        {
            return Run("Realm", $"JsOp.Get(Handle, {js}{byValue})", value, async);
        }
        var call = $"Realm.Get{fast}{(async ? "Async" : "")}(Handle, {js})";
        return value.Type.Names is not { } names ? (async ? $"Task<{value.Name}>" : value.Name, call)
            : async ? ($"async Task<{value.Name}>", $"{names}.FromJs(await {call})")
            : (value.Name, $"{names}.FromJs({call})");
    }

    private (string Type, string Body) Set(Value value, string js)
    {
        if (value.Opt || value.Type.Fast is null)
        {
            return Run("Realm", $"JsOp.Set(Handle, {js}, w => {value.Write("value")})", null, async);
        }
        var converted = value.Type.Names is { } names ? $"{names}.ToJs(value)" : "value";
        return async ? ("Task", $"Realm.SetAsync(Handle, {js}, {converted})") : ("void", $"Realm.Set(Handle, {js}, {converted})");
    }

    /// <summary>A method per overload (R23), with the caller's scope first when it returns a live object (§5).</summary>
    private void Method(string owner, MethodMember m)
    {
        var names = new Dictionary<string, string>();
        foreach (var signature in m.Signatures)
        {
            var (result, returns, promise) = Result(signature, Naming.OwnedType(owner, m.Name), owner);
            var name = Mode(Naming.Pascal(m.Name), promise);
            name = names.TryGetValue(name, out var claimed) ? claimed : names[name] = Claim(name, $"{owner}.{m.Name}");
            var (parameters, args) = Parameters(signature, owner + Naming.Pascal(m.Name), owner);
            var op = $"JsOp.Call(Handle, {Syntax.Literal(m.Name)}{args}{ByValue(returns)})";
            var (type, body) = Run("Realm", op, result, async || promise);
            Expression($"public {type} {name}({Join(result?.Type.Scoped == true ? "JsScope scope" : null, parameters)})", body);
        }
    }

    /// <summary>R24: an event, or a property watch; the scope owns the subscription.</summary>
    private void Subscription(string owner, string name, string? method, string js, IrType payload, string subject)
    {
        if (method is null)
        {
            throw new NotSupportedException($"{subject}: the profile names no method to subscribe with.");
        }
        var value = Slot(payload, Naming.OwnedType(owner, js), owner, optional: false);
        usesSystem = true;
        var (type, subscribe) = async ? ("Task<IAsyncDisposable>", "SubscribeAsync") : ("IDisposable", "Subscribe");
        Expression($"public {type} {Claim(Mode(name), subject)}(JsScope scope, Action<{value.Name}> handler)",
            $"scope.{subscribe}(Handle, {Syntax.Literal(method)}, {Syntax.Literal(js)}, {value.Reader}, handler)");
    }

    private void ModuleBody(string name, string specifier, IEnumerable<FunctionDeclaration> functions)
    {
        taken.UnionWith([name, "Module"]);
        using (w.Block($"public static partial class {name}"))
        {
            Separate();
            w.Line($"private const string Module = {Syntax.Literal(specifier)};");
            foreach (var f in functions)
            {
                var names = new Dictionary<string, string>();
                foreach (var signature in f.Signatures)
                {
                    var (result, returns, promise) = Result(signature, Naming.OwnedType(name, f.Name), null);
                    var method = Mode(Naming.Pascal(f.Name), promise);
                    method = names.TryGetValue(method, out var claimed) ? claimed : names[method] = Claim(method, f.Name);
                    var (parameters, args) = Parameters(signature, name + Naming.Pascal(f.Name), null);
                    var scoped = result?.Type.Scoped == true;
                    var op = $"JsOp.Call(Module, {Syntax.Literal(f.Name)}{args}{ByValue(returns)})";
                    var (type, body) = Run(scoped ? "scope.Realm" : "realm", op, result, async || promise);
                    Expression($"public static {type} {method}({Join(scoped ? "JsScope scope" : "JsRealm realm", parameters)})", body);
                }
            }
        }
    }

    // --- Pieces ---

    /// <summary>A signature's result without its Promise (R8): <c>null</c> for none.</summary>
    private (Value? Result, IrType Type, bool Promise) Result(Signature signature, string owner, string? self)
    {
        var returns = signature.Returns!;
        var promise = false;
        if (returns is RefType { Module: null, Name: "Promise", Args: [var inner] })
        {
            (returns, promise) = (inner, true);
        }
        var result = returns is PrimitiveType { Name: "void" or "undefined" } ? null : Slot(returns, owner, self, optional: false);
        return (result, returns, promise);
    }

    /// <summary>R16: a result that mentions a value-like class is read by value.</summary>
    private string ByValue(IrType type) => types.ByValue(type) ? ", byValue: true" : "";

    /// <summary>
    /// The parameters (R22: an optional one is <c>= default</c>, undefined), and the argument writer for the op: one
    /// value per parameter, in order.
    /// </summary>
    private (string Parameters, string Args) Parameters(Signature signature, string owner, string? self)
    {
        var parameters = new List<string>();
        var writes = new List<string>();
        foreach (var p in signature.Params)
        {
            if (p.Rest)
            {
                throw new NotSupportedException($"{owner}: the rest parameter {p.Name} isn't supported yet.");
            }
            var value = Slot(p.Type, Naming.OwnedType(owner, p.Name), self, p.Optional);
            var local = Syntax.Local(Naming.Pascal(p.Name), "scope", "realm", "w", "r");
            parameters.Add($"{value.Name} {local}{(p.Optional ? " = default" : "")}");
            writes.Add(value.Write(local));
        }
        var args = writes switch
        {
            [] => "",
            [var one] => $", w => {one}",
            _ => $", w =>\n{{\n{string.Join("\n", writes.Select(x => $"    {x};"))}\n}}",
        };
        return (string.Join(", ", parameters), args);
    }

    private Value Slot(IrType type, string owner, string? self, bool optional)
    {
        var (mapped, nullable) = types.Slot(type, owner, self);
        return new(mapped, (nullable || optional) && !mapped.IsUnion);
    }

    /// <summary>
    /// The return type and body of a member that sends <paramref name="op"/> through <paramref name="realm"/>: it
    /// reads <paramref name="result"/>, or nothing, and returns a <c>Task</c> when <paramref name="task"/> is set.
    /// </summary>
    private (string Type, string Body) Run(string realm, string op, Value? result, bool task)
    {
        usesTask |= task;
        if (result is null)
        {
            return task ? ("Task", $"{realm}.SendAsync({op})") : ("void", $"{realm}.Send({op})");
        }
        var bang = result.MaybeNull ? "!" : "";
        return !task ? (result.Name, $"{realm}.Call({op}, {result.Reader}){bang}")
            : result.MaybeNull ? ($"async Task<{result.Name}>", $"(await {realm}.CallAsync({op}, {result.Reader}))!")
            : ($"Task<{result.Name}>", $"{realm}.CallAsync({op}, {result.Reader})");
    }

    /// <summary>An expression-bodied member: on one line if it fits, otherwise its body on the next.</summary>
    private void Expression(string header, string body)
    {
        Separate();
        if (!body.Contains('\n') && 4 + header.Length + 4 + body.Length + 1 <= LineLength)
        {
            w.Line($"{header} => {body};");
            return;
        }
        w.Line(header + " =>");
        using (w.Indent())
        {
            var lines = body.Split('\n');
            foreach (var line in lines[..^1])
            {
                w.Line(line);
            }
            w.Line(lines[^1] + ";");
        }
    }

    /// <summary>A blank line between members.</summary>
    private void Separate()
    {
        if (!first)
        {
            w.Line();
        }
        first = false;
    }

    /// <summary>R26: the name in this mode; async members, and Promise methods in both modes (R8), end in <c>Async</c>.</summary>
    private string Mode(string name, bool promise = false) => async || promise ? name + "Async" : name;

    private string Claim(string name, string subject) => Naming.Member(taken, name, subject, diagnostics);

    private static string Join(string? first, string rest) =>
        first is null ? rest : rest.Length == 0 ? first : $"{first}, {rest}";
}
