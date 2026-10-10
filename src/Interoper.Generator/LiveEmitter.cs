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
    private readonly string ns;
    private readonly ICollection<Diagnostic> diagnostics;
    private readonly CodeWriter w = new();
    private readonly HashSet<string> taken = [], hidden = [];
    /// <summary>R26: the members this class declares, and those its emitted bases do, by name and parameter types.</summary>
    private readonly HashSet<string> declared = [], inherited = [];
    private bool usesSystem, usesTask, usesStreams, first = true;
    /// <summary>The live class being emitted, as its members name it (for <c>this</c>); none for a module.</summary>
    private CsType? self;

    private LiveEmitter(TypeMap types, Profile profile, GeneratorOptions options, ICollection<Diagnostic> diagnostics, string ns)
    {
        this.types = types;
        this.profile = profile;
        async = options.Api == Api.Async;
        usesTask = async;
        this.ns = ns;
        this.diagnostics = diagnostics;
    }

    /// <summary>A value as a member, parameter or result has it: <c>Opt&lt;T&gt;</c> when it can be undefined or null (R3).</summary>
    private sealed record Value(CsType Type, bool Opt)
    {
        public string Name => Opt ? $"Opt<{Type.Name}>" : Type.Name;

        /// <summary>The type as it tells members apart (R26), spelled the same in every namespace.</summary>
        public string Key => Opt ? $"Opt<{Type.FullName}>" : Type.FullName;

        public string Reader => Opt ? $"{Type.Static}(ref r) => JsJson.ReadOpt(ref r, {Type.Reader(1)})" : Type.Reader(0);

        public string Write(string value) => Opt ? $"JsJson.WriteValue(w, {value}, {Type.Writer(1)})" : Type.WriteCall("w", value, 1);

        /// <summary>The realm reads a <c>T?</c>; a non-null reference type needs a <c>!</c>.</summary>
        public bool MaybeNull => !Opt && !Type.IsValueType;
    }

    /// <summary>
    /// A live class (§5): its properties, methods, events (R24) and a watch per property, <c>Create</c> unless it's abstract (R18), and
    /// <c>Snapshot</c>/<c>Apply</c>. An abstract value-like class is closed (R4). A member its emitted bases also declare,
    /// with the same parameter types, is <c>new</c> (R26).
    /// </summary>
    /// <param name="declared">What each class declares, by name and parameter types, as far as known: filled as needed.</param>
    public static string Class(TypeMap types, Profile profile, GeneratorOptions options, ICollection<Diagnostic> diagnostics,
        Dictionary<ClassDeclaration, IReadOnlySet<string>> declared, ClassDeclaration c)
    {
        var e = new LiveEmitter(types, profile, options, diagnostics, types.Namespace(c));
        for (var b = types.LiveBase(c); b is not null; b = types.LiveBase(b))
        {
            if (!declared.TryGetValue(b, out var members))
            {
                // What a base declares is what emitting it declares; its diagnostics are reported where it's emitted.
                var emitted = new LiveEmitter(types, profile, options, [], types.Namespace(b));
                emitted.ClassBody(b);
                declared[b] = members = emitted.declared;
            }
            e.inherited.UnionWith(members);
        }
        e.ClassBody(c);
        declared[c] = e.declared;
        return e.File();
    }

    /// <summary>A module's functions, as a static class named after its file, in the module's namespace (§5, R27).</summary>
    public static string Module(TypeMap types, Profile profile, GeneratorOptions options, ICollection<Diagnostic> diagnostics,
        string name, string specifier, IEnumerable<FunctionDeclaration> functions)
    {
        var e = new LiveEmitter(types, profile, options, diagnostics, types.Namespace(specifier));
        e.ModuleBody(name, specifier, functions);
        return e.File();
    }

    /// <summary>The name of a module's static class: its file name (<c>measure.js</c> → <c>Measure</c>).</summary>
    public static string ModuleClass(string specifier) => Naming.Type(Path.GetFileNameWithoutExtension(specifier));

    private string File()
    {
        var usings = new[]
        {
            usesSystem ? "System" : null, usesStreams ? "System.Collections.Generic" : null, usesStreams ? "System.Threading" : null,
            usesTask ? "System.Threading.Tasks" : null, "Interoper",
        };
        return CodeWriter.CSharp(ns, [.. usings.OfType<string>()]) + w.ToString();
    }

    private void ClassBody(ClassDeclaration c)
    {
        var name = Naming.Type(c.Name);
        var snapshot = TypeMap.SnapshotName(c);
        var snapshotKey = CsType.Record(new(ns, snapshot)).FullName;
        self = types.Self(c);
        var modifier = types.IsClosed(c) ? "closed " : c.Abstract ? "abstract " : types.HasSubclasses(c) ? "" : "sealed ";
        taken.UnionWith([name, "Realm", "Handle", "Write", Mode("Snapshot"), Mode("Apply")]);
        if (!async)
        {
            Hide(c);
        }
        // Mapping §2: a generic class's type parameters are entities.
        var constraints = string.Concat(c.TypeParameters.Select(t => $" where {t.Name} : JsObject"));
        using (w.Block($"public {modifier}partial class {self.Name} : {types.LiveBaseName(c, ns)}{constraints}"))
        {
            Separate();
            w.Line($"internal {name}(JsRealm realm, int handle) : base(realm, handle) {{ }}");
            if (types.Constructible(c))
            {
                var (type, body) = Run("scope.Realm", $"JsOp.Create({types.ClassId(c, ns)}, w => {snapshot}.WriteMembers(w, props))", new(self, false), async);
                var create = Claim(Mode("Create"), c.Name);
                Expression($"public static {Declare(create, ["JsScope", snapshotKey])}{type} {create}(JsScope scope, {snapshot} props)", body);
            }
            foreach (var p in c.Members.OfType<PropertyMember>().Where(p => p.Type is not LiteralType)) // R20: a constant is no property
            {
                Property(name, p);
            }
            foreach (var m in c.Members.OfType<MethodMember>())
            {
                Calls(name, m.Name, m.Signatures, name);
            }
            foreach (var e in c.Events)
            {
                Subscription(name, "On" + Naming.Pascal(e.Name), profile.Events!.Subscribe!, e.Name, e.Type, $"{name}.on:{e.Name}", optional: false, isEvent: true);
            }
            // §5; a callback can't be read back (R21).
            foreach (var p in types.HasWatches(c) ? c.Members.OfType<PropertyMember>().Where(p => p.Type is not LiteralType && !types.IsCallback(p)) : [])
            {
                Subscription(name, "Watch" + Naming.Pascal(p.Name), profile.Watch!, p.Name, p.Type, $"{name}.watch:{p.Name}", p.Optional, isEvent: false);
            }
            var (snapshotType, read) = Run("Realm", "JsOp.Snapshot(Handle)", new(CsType.Record(new(ns, snapshot)), false), async);
            Expression($"public {Declare(Mode("Snapshot"), [])}{snapshotType} {Mode("Snapshot")}()", read);
            var (applyType, apply) = Run("Realm", $"JsOp.Apply(Handle, w => {snapshot}.WriteMembers(w, patch))", null, async);
            Expression($"public {Declare(Mode("Apply"), [snapshotKey])}{applyType} {Mode("Apply")}({snapshot} patch)", apply);
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
        if (types.IsCallback(p))
        {
            CallbackProperty(subject, js, x, set!);
            return;
        }
        var (getType, getter) = Get(get, js, ByValue(p.Type));
        var (setType, setter) = set is null ? default : Set(set, js);
        if (async || get.Type.Scoped)
        {
            var (getName, scope) = (Claim(Mode("Get" + x), subject), get.Type.Scoped ? "JsScope scope" : "");
            Expression($"public {Declare(getName, get.Type.Scoped ? ["JsScope"] : [])}{getType} {getName}({scope})", getter);
            if (set is not null)
            {
                var setName = Claim(Mode("Set" + x), subject);
                Expression($"public {Declare(setName, [set.Key])}{setType} {setName}({set.Name} value)", setter);
            }
        }
        else if (set is null || set.Name != get.Name)
        {
            var getName = Claim(x, subject);
            Expression($"public {Declare(getName)}{getType} {getName}", getter);
            if (set is not null)
            {
                var setName = Claim("Set" + x, subject);
                Expression($"public {Declare(setName, [set.Key])}void {setName}({set.Name} value)", setter);
            }
        }
        else
        {
            Separate();
            var getName = Claim(x, subject);
            using (w.Block($"public {Declare(getName)}{getType} {getName}"))
            {
                w.Line($"get => {getter};");
                w.Line($"set => {setter};");
            }
        }
    }

    /// <summary>
    /// R21: a callback property is setter-only, <c>SetX(scope, value)</c>, since JS can't hand a function back. One
    /// whose result JS uses is skipped in async mode (IOP2005).
    /// </summary>
    private void CallbackProperty(string subject, string js, string x, Value set)
    {
        if (async && set.Type.Returns)
        {
            SyncOnly(subject);
            return;
        }
        var setName = Claim(Mode("Set" + x), subject);
        var (setType, setter) = Run("Realm", $"JsOp.Set(Handle, {js}, w => {set.Write("value")})", null, async);
        Expression($"public {Declare(setName, ["JsScope", set.Key])}{setType} {setName}(JsScope scope, {set.Name} value)", setter);
    }

    /// <summary>R21: a member with a callback whose result JS uses, which async mode can't give it, is skipped there.</summary>
    private void SyncOnly(string subject)
    {
        var diagnostic = new Diagnostic(Diagnostic.SyncOnly, subject, "skipped in async mode: JS uses its callback's result (R21)");
        if (!diagnostics.Contains(diagnostic))
        {
            diagnostics.Add(diagnostic);
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

    /// <summary>
    /// A method per overload (R23): a live class's (<paramref name="self"/>), or a module's static one, which takes the
    /// realm. Either takes the caller's scope first instead when it returns a live object or takes a callback (§5, R21);
    /// one with a callback whose result JS uses is skipped in async mode (IOP2005). <paramref name="export"/> is the name
    /// JS calls, when it isn't <paramref name="js"/>: a module's default export.
    /// </summary>
    private void Calls(string owner, string js, IReadOnlyList<Signature> signatures, string? self, string? export = null)
    {
        var names = new Dictionary<string, string>();
        foreach (var signature in signatures)
        {
            var (result, returns, promise) = Result(signature, Naming.OwnedType(owner, js), self);
            var (parameters, args, keys, callbacks) = Parameters(signature, Naming.OwnedType(owner, js), self);
            if (async && callbacks.Any(c => c.Returns))
            {
                SyncOnly($"{owner}.{js}");
                continue;
            }
            var name = Mode(Naming.Pascal(js), promise);
            name = names.TryGetValue(name, out var claimed) ? claimed : names[name] = Claim(name, $"{owner}.{js}");
            var scoped = result?.Type.Scoped == true || callbacks.Count > 0;
            var op = $"JsOp.Call({(self is null ? "Module" : "Handle")}, {Syntax.Literal(export ?? js)}{args}{ByValue(returns)})";
            var (type, body) = Run(self is not null ? "Realm" : scoped ? "scope.Realm" : "realm", op, result, async || promise);
            var (first, firstType) = scoped ? ("JsScope scope", "JsScope") : self is null ? ("JsRealm realm", "JsRealm") : (null, null);
            var hides = Declare(name, firstType is null ? keys : keys.Prepend(firstType));
            Expression($"public {(self is null ? "static " : "")}{hides}{type} {name}({Join(first, parameters)})", body);
        }
    }

    /// <summary>
    /// R24: an event, with its <c>EventOptions</c>, or a property watch; the scope owns the subscription. An event whose
    /// payload is a data record sends only the record's members unless the options name others. Async mode adds a stream
    /// of each.
    /// </summary>
    private void Subscription(string owner, string name, string method, string js, IrType payload, string subject, bool optional, bool isEvent)
    {
        var value = Slot(payload, Naming.OwnedType(owner, js), owner, optional);
        usesSystem = true;
        var (type, subscribe) = async ? ("Task<IAsyncDisposable>", "SubscribeAsync") : ("IDisposable", "Subscribe");
        var fields = isEvent && types.Projection(payload) is { } projection ? $"[{string.Join(", ", projection.Select(Syntax.Literal))}]" : "null";
        var (options, optionsKey, sent) = isEvent ? (", EventOptions? options = null", new[] { "EventOptions" }, "options") : ("", [], "null");
        var target = $"Handle, {Syntax.Literal(method)}, {Syntax.Literal(js)}, {value.Reader}";
        var subscription = Claim(Mode(name), subject);
        Expression($"public {Declare(subscription, ["JsScope", $"Action<{value.Key}>", .. optionsKey])}{type} {subscription}(JsScope scope, Action<{value.Name}> handler{options})",
            $"scope.{subscribe}({target}, handler, {sent}, {fields})");
        if (async)
        {
            usesStreams = true;
            var stream = Claim(name + "StreamAsync", subject);
            Expression($"public {Declare(stream, ["JsScope", .. optionsKey, "CancellationToken"])}IAsyncEnumerable<{value.Name}> {stream}(JsScope scope{options}, CancellationToken cancellationToken = default)",
                $"scope.StreamAsync({target}, {sent}, {fields}, cancellationToken)");
        }
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
                Calls(name, f.Name, f.Signatures, null, f.Export);
            }
        }
    }

    // --- Pieces ---

    /// <summary>A signature's result without its Promise (R8): <c>null</c> for none.</summary>
    private (Value? Result, IrType Type, bool Promise) Result(Signature signature, string owner, string? self)
    {
        var returns = signature.Returns ?? new PrimitiveType("void"); // none, as for a constructor
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
    /// The parameters (R22: an optional one is <c>= default</c>, undefined; a rest one is <c>params T[]</c>), their types
    /// as they tell members apart (R26), and the argument writer for the op: one value per parameter, in order, leaving
    /// out trailing undefined ones, then one per element of the rest parameter (a spread).
    /// </summary>
    private (string Parameters, string Args, IEnumerable<string> Keys, List<CsType> Callbacks) Parameters(Signature signature, string owner, string? self)
    {
        string[] used = signature.Params is [.., { Rest: true }] ? ["scope", "realm", "w", "r", "item"] : ["scope", "realm", "w", "r"];
        var values = signature.Params.Select(p => (p.Optional, Spread: p.Rest, Value: Slot(p.Type, Naming.OwnedType(owner, p.Name), self, p.Optional),
            Local: Syntax.Local(Naming.Pascal(p.Name), used))).ToList();
        var parameters = values.Select(p => $"{(p.Spread ? "params " : "")}{p.Value.Name} {p.Local}{(p.Optional ? " = default" : "")}");
        // An optional parameter is written while it or a later one is defined, or the rest has elements; all after it
        // are optional or the rest.
        var writes = values.Select((p, i) =>
            p.Spread ? $"foreach (var item in {p.Local}) {p.Value.Type.Element!.WriteCall("w", "item", 1)}"
            : !p.Optional ? p.Value.Write(p.Local)
            : $"if ({string.Join(" || ", values.Skip(i).Select(q => q.Spread ? $"{q.Local}.Length > 0" : q.Value.Type.IsCallback ? $"{q.Local} is not null" : $"!{q.Local}.IsUndefined"))}) {p.Value.Write(p.Local)}").ToList();
        var args = writes switch
        {
            [] => "",
            [var one] when !values[0].Optional && !values[0].Spread => $", w => {one}",
            _ => $", w =>\n{{\n{string.Join("\n", writes.Select(x => $"    {x};"))}\n}}",
        };
        return (string.Join(", ", parameters), args, values.Select(p => p.Value.Key), [.. values.Select(p => p.Value.Type).Where(t => t.IsCallback)]);
    }

    /// <summary>A value in this class (<paramref name="owner"/>) or module; a callback is never <c>Opt</c>, but nullable (R21).</summary>
    private Value Slot(IrType type, string owner, string? self, bool optional)
    {
        var (mapped, nullable) = types.Slot(type, new(ns, owner), self is null ? null : this.self);
        var named = mapped.In(ns).Unhidden(hidden);
        usesSystem |= named.IsCallback; // Action, Func
        return named.IsCallback ? new(nullable || optional ? named.Nullable() : named, false) : new(named, (nullable || optional) && !mapped.IsUnion);
    }

    /// <summary>
    /// The names of a sync class's members, and its bases', that hide a type of that name: all but properties of exactly
    /// that type. <c>Opt&lt;Cursor&gt; Cursor</c> reads with <c>global::Ns.Cursor.Read</c> (CS0120).
    /// </summary>
    private void Hide(ClassDeclaration c)
    {
        for (var k = c; k is not null; k = types.LiveBase(k))
        {
            var owner = types.Name(k);
            foreach (var m in k.Members)
            {
                var name = Naming.Pascal(m.Name);
                if (m is not PropertyMember p || p.Type is not LiteralType && types.Slot(p.Type, owner.Owned(p.Name), types.Self(k)) is var (type, nullable) &&
                    !type.Scoped && new Value(type.In(ns), (nullable || p.Optional) && !type.IsUnion).Name != name)
                {
                    hidden.Add(name);
                }
            }
        }
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

    /// <summary>
    /// R26: records a member this class declares, by its name and parameter types (<paramref name="parameters"/> is null
    /// for a property), and returns its <c>new</c> modifier when it hides a member an emitted base declares (CS0108): one
    /// of the same name and parameter types, a property of its name, or for a property, any member of its name.
    /// </summary>
    private string Declare(string name, IEnumerable<string>? parameters = null)
    {
        // A property is its bare name; a method is its signature, plus its name with `(` for a property to find.
        var keys = parameters is null ? [name] : new[] { $"{name}({string.Join(", ", parameters)})", name + "(" };
        declared.UnionWith(keys);
        var hides = parameters is null ? inherited.Contains(name) || inherited.Contains(name + "(") : inherited.Contains(keys[0]) || inherited.Contains(name);
        return hides ? "new " : "";
    }

    private static string Join(string? first, string rest) =>
        first is null ? rest : rest.Length == 0 ? first : $"{first}, {rest}";
}
