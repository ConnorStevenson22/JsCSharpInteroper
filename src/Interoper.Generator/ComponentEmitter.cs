using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// R28: a Blazor component per custom element, as a plain C# class with its own <c>BuildRenderTree</c> (no Razor file),
/// and the <c>[EventHandler]</c> registrations of the elements' custom events (<c>{Package}Events.cs</c>).
/// </summary>
internal sealed class ComponentEmitter
{
    private const string Components = "global::Microsoft.AspNetCore.Components";
    private const string Task = "global::System.Threading.Tasks.Task";

    /// <summary>
    /// What a parameter can't be named: the component's own members, and those of <c>JsComponentBase</c>,
    /// <c>ComponentBase</c> and <c>object</c> it would hide (CS0108).
    /// </summary>
    private static readonly string[] Reserved =
    [
        "Element", "OnElementReady", "ChildContent", "AdditionalAttributes", "Scope", "IsDisposed", "ElementAsync", "DisposeAsync",
        "SetParametersAsync", "BuildRenderTree", "OnInitialized", "OnInitializedAsync", "OnParametersSet", "OnParametersSetAsync",
        "OnAfterRender", "OnAfterRenderAsync", "ShouldRender", "StateHasChanged", "InvokeAsync", "DispatchExceptionAsync", "Assets",
        "AssignedRenderMode", "RendererInfo", "ToString", "GetHashCode", "Equals", "GetType",
    ];

    private readonly TypeMap types;
    private readonly bool async;
    private readonly string ns;
    private readonly ICollection<Diagnostic> diagnostics;
    private readonly HashSet<string> taken = [];
    private readonly CodeWriter w;
    private int sequence;

    private ComponentEmitter(TypeMap types, GeneratorOptions options, ICollection<Diagnostic> diagnostics, string ns)
    {
        this.types = types;
        async = options.Api == Api.Async;
        this.ns = ns;
        this.diagnostics = diagnostics;
        w = CodeWriter.CSharp(ns);
    }

    /// <summary>
    /// A parameter for an attribute or a writable property. An <paramref name="Attribute"/> one renders as the HTML
    /// attribute of that name; any other is set on the live element (<paramref name="Js"/>). A null one is JS null
    /// when the type has null (<paramref name="JsNull"/>), and undefined when it's only optional.
    /// </summary>
    private sealed record Parameter(string Name, CsType Type, bool Nullable, bool JsNull, string Js, string? Attribute)
    {
        /// <summary>No <c>Opt</c>: a parameter the parent doesn't supply is neither rendered nor set.</summary>
        public string TypeName => Nullable && !Type.IsUnion ? Type.Name + "?" : Type.Name;

        public string Sent => "sent" + Name;

        /// <summary>The values sent for a bound property, whose change events are echoes (<c>JsEchoes</c>).</summary>
        public string Echoes => "echoes" + Name;
    }

    /// <summary>
    /// An event's <c>EventCallback</c> (none for a detail), the handler that raises it, and the property it binds
    /// (<c>@bind-Zoom</c>), if any: <c>ZoomChanged</c>, with the detail's member of that name.
    /// </summary>
    private sealed record Callback(ElementEvent Event, string Name, string Handler, string? Detail, string? Reader, bool Scoped,
        (Parameter Property, string Changed, string Member)? Bind);

    /// <summary>The component of an element (R28), in its class's namespace, named <paramref name="name"/>.</summary>
    public static string Component(TypeMap types, GeneratorOptions options, ICollection<Diagnostic> diagnostics, SelectedElement element, string name)
    {
        var e = new ComponentEmitter(types, options, diagnostics, types.Namespace(element.Class));
        e.Body(element, name);
        return e.w.ToString();
    }

    /// <summary>
    /// The <c>[EventHandler]</c> attribute of each custom event, so markup that imports the namespace can use
    /// <c>@ondemo-map-click</c>: Blazor's own pipeline delivers it, once interoper.js has registered it (design §3.4).
    /// Razor looks for them only on a class named <c>EventHandlers</c>.
    /// </summary>
    public static string Events(string ns, IEnumerable<string> events)
    {
        var w = CodeWriter.CSharp(ns);
        foreach (var e in events)
        {
            w.Line($"[{Components}.EventHandler({Syntax.Literal(Attribute(e))}, typeof(global::Interoper.JsEventArgs), true, true)]");
        }
        using (w.Block("public static partial class EventHandlers"))
        {
        }
        return w.ToString();
    }

    /// <summary>The attribute Blazor names an event handler with: <c>on</c> plus the event name in lower case.</summary>
    public static string Attribute(string eventName) => "on" + eventName.ToLowerInvariant();

    private void Body(SelectedElement s, string name)
    {
        var c = s.Class;
        var element = types.Name(c).In(ns);
        taken.UnionWith([name, .. Reserved]);
        var parameters = Parameters(s, name);
        // A parameter named like a type it doesn't have hides that type's static methods: Opt<Cursor>? Cursor (CS0120).
        var hidden = parameters.Where(p => p.TypeName != p.Name).Select(p => p.Name).ToHashSet();
        parameters = [.. parameters.Select(p => p with { Type = p.Type.Unhidden(hidden) })];
        var callbacks = Callbacks(s, name, parameters, hidden);
        var bound = callbacks.Where(c => c.Bind is not null).Select(c => c.Bind!.Value.Property.Name).ToHashSet();
        var slots = s.Element.Slots.Where(slot => slot.Name.Length > 0).Select(slot => (slot.Name, Parameter: Claim(Naming.Pascal(slot.Name), $"{name}.{slot.Name}"))).ToList();

        using (w.Block($"public sealed partial class {name} : global::Interoper.JsComponentBase"))
        {
            w.Line($"private readonly global::System.Collections.Generic.HashSet<string> supplied = new(global::System.StringComparer.OrdinalIgnoreCase);");
            w.Line($"private {Components}.ElementReference element;");
            foreach (var p in parameters.Where(p => p.Attribute is null))
            {
                w.Line($"private (bool Done, {p.TypeName} Value) {p.Sent};");
            }
            foreach (var p in parameters.Where(p => bound.Contains(p.Name)))
            {
                w.Line($"private readonly global::Interoper.JsEchoes<{p.TypeName}> {p.Echoes} = new();");
            }
            w.Line();
            foreach (var p in parameters)
            {
                var initial = p.Nullable || p.Type.IsValueType ? "" : " = default!;";
                w.Line($"[{Components}.Parameter] public {p.TypeName} {p.Name} {{ get; set; }}{initial}");
            }
            foreach (var callback in callbacks)
            {
                var type = callback.Detail is null ? $"{Components}.EventCallback" : $"{Components}.EventCallback<{callback.Detail}>";
                w.Line($"[{Components}.Parameter] public {type} {callback.Name} {{ get; set; }}");
                if (callback.Bind is var (property, changed, _))
                {
                    w.Line($"[{Components}.Parameter] public {Components}.EventCallback<{property.TypeName}> {changed} {{ get; set; }}");
                }
            }
            w.Line($"[{Components}.Parameter] public {Components}.RenderFragment? ChildContent {{ get; set; }}");
            foreach (var (_, parameter) in slots)
            {
                w.Line($"[{Components}.Parameter] public {Components}.RenderFragment? {parameter} {{ get; set; }}");
            }
            w.Line($"[{Components}.Parameter(CaptureUnmatchedValues = true)] public global::System.Collections.Generic.IReadOnlyDictionary<string, object>? AdditionalAttributes {{ get; set; }}");
            w.Line($"[{Components}.Parameter] public {Components}.EventCallback<{element}> OnElementReady {{ get; set; }}");
            w.Line();
            w.Line($"public {element}? Element {{ get; private set; }}");
            w.Line();

            // What the parent supplied, which is all that's rendered or set.
            using (w.Block($"public override {Task} SetParametersAsync({Components}.ParameterView parameters)"))
            {
                w.Line("supplied.Clear();");
                using (w.Block("foreach (var parameter in parameters)"))
                {
                    w.Line("supplied.Add(parameter.Name);");
                }
                w.Line("return base.SetParametersAsync(parameters);");
            }
            w.Line();
            Render(s.Element.Tag, parameters, callbacks, slots, bound);
            w.Line();
            AfterRender(element, types.Import(s.Class)!.Value.Module, parameters, bound);
            foreach (var callback in callbacks)
            {
                w.Line();
                Handler(callback);
            }
        }
    }

    /// <summary>
    /// The attributes, in the manifest's order, then the other writable properties, its own and its bases', in the
    /// class's order. A callback or a DOM element isn't one: set it through <c>Element</c>.
    /// </summary>
    private List<Parameter> Parameters(SelectedElement s, string component)
    {
        var properties = new List<(PropertyMember Property, ClassDeclaration Owner)>();
        for (var k = s.Class; k is not null; k = types.LiveBase(k))
        {
            properties.AddRange(k.Members.OfType<PropertyMember>()
                .Where(p => !p.Readonly && !p.Static && p.Type is not (LiteralType or ThisType) && !types.IsCallback(p) && !types.IsElement(p) && properties.All(x => x.Property.Name != p.Name))
                .Select(p => (p, k)));
        }
        var result = new List<Parameter>();
        Parameter Property(PropertyMember p, ClassDeclaration owner, ElementAttribute? attribute)
        {
            var type = p.WriteType ?? p.Type;
            var ownerName = types.Name(owner);
            var (mapped, nullable) = types.Slot(type, ownerName with { Name = Naming.OwnedType(ownerName.Name, p.Name, input: p.WriteType is not null) });
            mapped = mapped.In(ns);
            // A property of another type is still an HTML attribute when the manifest gives the attribute a primitive one.
            if (mapped.Fast is null && attribute?.Type is PrimitiveType { Name: "string" or "number" or "boolean" } primitive)
            {
                (mapped, type) = (types.Slot(primitive, ownerName).Type, primitive);
            }
            return new(Claim(Naming.Pascal(p.Name), $"{component}.{p.Name}"), mapped, nullable || p.Optional, HasNull(type), p.Name, mapped.Fast is null ? null : attribute?.Name);
        }
        foreach (var a in s.Element.Attributes)
        {
            if (a.Property is { } name)
            {
                if (properties.FirstOrDefault(x => x.Property.Name == name) is ({ } p, var owner) && result.All(r => r.Js != name))
                {
                    result.Add(Property(p, owner, a));
                }
                continue;
            }
            var (mapped, nullable) = types.Slot(a.Type!, new(ns, component + Naming.Pascal(a.Name)));
            result.Add(new(Claim(Naming.Pascal(a.Name), $"{component}.{a.Name}"), mapped.In(ns), nullable, HasNull(a.Type!), a.Name, a.Name));
        }
        foreach (var (p, owner) in properties.Where(x => result.All(r => r.Js != x.Property.Name)))
        {
            result.Add(Property(p, owner, null));
        }
        return result;
    }

    private static bool HasNull(IrType type) => type is PrimitiveType { Name: "null" } || type is UnionType u && u.Types.Any(HasNull);

    /// <summary>
    /// An event's callback, and the property it binds: an event named <c>…-zoom-change</c> or <c>…zoomChange</c>
    /// whose detail has a member <c>zoom</c> of the property's type gives <c>ZoomChanged</c>, for <c>@bind-Zoom</c>.
    /// The longest property name that matches wins: <c>max-zoom-change</c> binds <c>maxZoom</c> over <c>zoom</c>.
    /// </summary>
    private List<Callback> Callbacks(SelectedElement s, string component, List<Parameter> parameters, IReadOnlySet<string> hidden)
    {
        var result = new List<Callback>();
        var bound = new HashSet<Parameter>();
        foreach (var e in s.Element.Events)
        {
            var pascal = Naming.Pascal(e.Name);
            var subject = $"{component}.on:{e.Name}";
            var name = Claim("On" + pascal, subject);
            var handler = Claim($"Raise{pascal}Async", subject);
            if (e.Detail is not { } detail)
            {
                result.Add(new(e, name, handler, null, null, false, null));
                continue;
            }
            var (mapped, nullable) = types.Slot(detail, new(ns, pascal + "Detail"));
            mapped = mapped.In(ns).Unhidden(hidden);
            var opt = nullable && !mapped.IsUnion;
            var reader = opt ? $"{mapped.Static}(ref r) => global::Interoper.JsJson.ReadOpt(ref r, {mapped.Reader(1)})" : mapped.Reader(0);
            var typeName = opt ? $"global::Interoper.Opt<{mapped.Name}>" : mapped.Name;
            (Parameter, string, string)? bind = null;
            if (!opt && detail is RefType r && types.Declaration(r) is { } record and (InterfaceDeclaration or AliasDeclaration { Type: ObjectType }))
            {
                var members = types.Fields(record).Where(f => f.Constant is null && !f.Opt).ToList();
                foreach (var p in parameters.Where(p => !bound.Contains(p) && !p.Nullable && Changes(e.Name, p.Js)).OrderByDescending(p => p.Js.Length))
                {
                    if (members.FirstOrDefault(f => f.JsName == p.Js && f.Type!.FullName == p.Type.FullName) is { } member)
                    {
                        bound.Add(p);
                        bind = (p, Claim(p.Name + "Changed", subject), member.Name);
                        break;
                    }
                }
            }
            result.Add(new(e, name, handler, typeName, reader, mapped.Scoped, bind));
        }
        return result;
    }

    /// <summary>Whether an event reports a change of property <paramref name="js"/>: <c>…-zoom-change</c> or <c>…zoomChange</c>.</summary>
    private static bool Changes(string eventName, string js)
    {
        var kebab = string.Concat(js.Select((ch, i) => char.IsUpper(ch) ? (i == 0 ? "" : "-") + char.ToLowerInvariant(ch) : ch.ToString()));
        return eventName == $"{kebab}-change" || eventName.EndsWith($"-{kebab}-change", StringComparison.Ordinal) ||
            eventName == $"{js}Change" || eventName.EndsWith($"{Naming.Pascal(js)}Change", StringComparison.Ordinal);
    }

    /// <summary>
    /// The element: the supplied attributes, which prerendering and static SSR render too; the event handlers; the
    /// element reference; each named slot's content in a <c>div</c> with that slot (<c>display:contents</c>), and the
    /// default slot's.
    /// </summary>
    private void Render(string tag, List<Parameter> parameters, List<Callback> callbacks, List<(string Name, string Parameter)> slots, HashSet<string> bound)
    {
        using (w.Block($"protected override void BuildRenderTree({Components}.Rendering.RenderTreeBuilder builder)"))
        {
            w.Line($"builder.OpenElement({sequence++}, {Syntax.Literal(tag)});");
            w.Line($"builder.AddMultipleAttributes({sequence++}, AdditionalAttributes);");
            foreach (var p in parameters.Where(p => p.Attribute is not null))
            {
                var add = $"builder.AddAttribute({sequence++}, {Syntax.Literal(p.Attribute!)}, {AttributeValue(p)});";
                if (!bound.Contains(p.Name))
                {
                    w.Line($"if (supplied.Contains(nameof({p.Name}))) {add}");
                    continue;
                }
                using (w.Block($"if (supplied.Contains(nameof({p.Name})))"))
                {
                    w.Line($"{p.Echoes}.Sent({p.Name});");
                    w.Line(add);
                }
            }
            foreach (var callback in callbacks)
            {
                var when = callback.Bind is var (_, changed, _) ? $"{callback.Name}.HasDelegate || {changed}.HasDelegate" : $"{callback.Name}.HasDelegate";
                w.Line($"if ({when}) builder.AddAttribute({sequence++}, {Syntax.Literal(Attribute(callback.Event.Name))}, " +
                    $"{Components}.EventCallback.Factory.Create<global::Interoper.JsEventArgs>(this, {callback.Handler}));");
            }
            w.Line($"builder.AddElementReferenceCapture({sequence++}, value => element = value);");
            foreach (var (slot, parameter) in slots)
            {
                using (w.Block($"if ({parameter} is not null)"))
                {
                    w.Line($"builder.OpenElement({sequence++}, \"div\");");
                    w.Line($"builder.AddAttribute({sequence++}, \"slot\", {Syntax.Literal(slot)});");
                    w.Line($"builder.AddAttribute({sequence++}, \"style\", \"display:contents\");");
                    w.Line($"builder.AddContent({sequence++}, {parameter});");
                    w.Line("builder.CloseElement();");
                }
            }
            w.Line($"builder.AddContent({sequence++}, ChildContent);");
            w.Line("builder.CloseElement();");
        }
    }

    /// <summary>An attribute's text: a number in the invariant culture, a string enum's JS value; a bool is present or absent.</summary>
    private static string AttributeValue(Parameter p) => (p.Type.Fast, p.Type.Names, p.Nullable) switch
    {
        ("Number", _, false) => $"{p.Name}.ToString(global::System.Globalization.CultureInfo.InvariantCulture)",
        ("Number", _, true) => $"{p.Name}?.ToString(global::System.Globalization.CultureInfo.InvariantCulture)",
        ("Boolean", _, true) => $"{p.Name} == true",
        (_, { } names, false) => $"{names}.ToJs({p.Name})",
        (_, { } names, true) => $"{p.Name} is {{ }} value ? {names}.ToJs(value) : null",
        _ => p.Name,
    };

    /// <summary>
    /// Once interactive: the element's live object, through the async seam in both modes (async mode loads its module
    /// first; sync mode preloaded it); then each supplied property that isn't an attribute, set when it's new or
    /// changed (<c>JsDiff.Same</c>), through the mode's seam; then, the first time, <c>OnElementReady</c>. It stops
    /// when the component is disposed meanwhile.
    /// </summary>
    private void AfterRender(string element, string module, List<Parameter> parameters, HashSet<string> bound)
    {
        using (w.Block($"protected override async {Task} OnAfterRenderAsync(bool firstRender)"))
        {
            using (w.Block("if (firstRender)"))
            {
                w.Line($"Element = await ElementAsync<{element}>(element{(async ? ", " + Syntax.Literal(module) : "")});");
            }
            using (w.Block("if (Element is null)"))
            {
                w.Line("return;");
            }
            foreach (var p in parameters.Where(p => p.Attribute is null))
            {
                using (w.Block($"if (supplied.Contains(nameof({p.Name})) && !({p.Sent}.Done && global::Interoper.JsDiff.Same({p.Sent}.Value, {p.Name})))"))
                {
                    const string value = "value";
                    w.Line($"var {value} = {p.Name};"); // the value sent, whatever the parameter is by the time it's sent
                    if (bound.Contains(p.Name))
                    {
                        w.Line($"{p.Echoes}.Sent({value});");
                    }
                    var op = $"global::Interoper.JsOp.Set(Element.Handle, {Syntax.Literal(p.Js)}, w => {Write(p, value)})";
                    w.Line(async ? $"await Scope.Realm.SendAsync({op});" : $"Scope.Realm.Send({op});");
                    w.Line($"{p.Sent} = (true, {value});");
                    if (async)
                    {
                        using (w.Block("if (IsDisposed)"))
                        {
                            w.Line("return;");
                        }
                    }
                }
            }
            using (w.Block("if (firstRender)"))
            {
                w.Line("await OnElementReady.InvokeAsync(Element);");
            }
        }
    }

    /// <summary>
    /// Writes a parameter's value; a null one as JS null when its type has null, else as undefined (only optional); a
    /// union has those states itself (R2).
    /// </summary>
    private static string Write(Parameter p, string value) =>
        p.Nullable && !p.Type.IsUnion
            ? $"{{ if ({value} is {{ }} v) {p.Type.WriteCall("w", "v", 1)}; else {(p.JsNull ? "w.WriteNullValue()" : "global::Interoper.JsJson.WriteUndefined(w)")}; }}"
            : p.Type.WriteCall("w", value, 1);

    /// <summary>
    /// Raises an event's callback with its detail, read with the component's scope; and the bound property's, unless
    /// the event is an echo of a value the component sent (<c>JsEchoes</c>).
    /// </summary>
    private void Handler(Callback callback)
    {
        string Header(bool isAsync) => $"private {(isAsync ? "async " : "")}{Task} {callback.Handler}(global::Interoper.JsEventArgs e)";
        if (callback.Detail is null)
        {
            w.Line($"{Header(false)} => {callback.Name}.InvokeAsync();");
            return;
        }
        if (callback.Bind is null && !callback.Scoped)
        {
            w.Line($"{Header(false)} =>");
            using (w.Indent())
            {
                w.Line($"{callback.Name}.InvokeAsync(e.Read({callback.Reader}));");
            }
            return;
        }
        using (w.Block(Header(true)))
        {
            if (callback.Scoped)
            {
                w.Line("var scope = Scope;");
            }
            w.Line($"var detail = e.Read({callback.Reader});");
            if (callback.Bind is var (property, changed, member))
            {
                w.Line($"var echo = {property.Echoes}.IsEcho(detail.{member});");
                w.Line($"await {callback.Name}.InvokeAsync(detail);");
                using (w.Block("if (!echo)"))
                {
                    w.Line($"await {changed}.InvokeAsync(detail.{member});");
                }
            }
            else
            {
                w.Line($"await {callback.Name}.InvokeAsync(detail);");
            }
        }
    }

    private string Claim(string name, string subject) => Naming.Member(taken, name, subject, diagnostics);
}
