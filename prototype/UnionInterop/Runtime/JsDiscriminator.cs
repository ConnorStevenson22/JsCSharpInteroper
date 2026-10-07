using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnionInterop.Runtime;

/// <summary>
/// Marks a generated type as the case of a TypeScript discriminated union,
/// e.g. <c>PointProperties &amp; { type: "point" }</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class JsDiscriminatorAttribute(string property, string value) : Attribute
{
    public string Property { get; } = property;
    public string Value { get; } = value;
}

/// <summary>
/// Marks a generated type as a proxy for a live JS object (serialized by Blazor as
/// <c>{"__jsObjectId": n}</c>). <paramref name="jsType"/> is matched against the
/// <c>__jsType</c> hint our JS shim adds so a union of several proxy types can be told apart.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class JsProxyTypeAttribute(string jsType) : Attribute
{
    public string JsType { get; } = jsType;
}

/// <summary>
/// One classifier for every generated union. The built-in classifiers cover token kinds
/// and property *names*; TypeScript unions mostly need property *values*
/// (<c>type: "point"</c>) and JS handles, which this adds:
/// <list type="number">
/// <item>JSON object with <c>__jsObjectId</c> → the proxy case (using <c>__jsType</c> if several).</item>
/// <item>JSON object with a discriminator property → the case whose <see cref="JsDiscriminatorAttribute"/> matches.</item>
/// <item>Otherwise: by token kind (string / number / bool / array / object), if exactly one case fits.</item>
/// </list>
/// </summary>
public sealed class JsUnionClassifier : JsonTypeClassifierFactory
{
    public override bool CanClassify(JsonTypeClassifierContext context) =>
        context.Kind == JsonTypeClassifierKind.Union;

    public override JsonTypeClassifier CreateJsonClassifier(JsonTypeClassifierContext context, JsonSerializerOptions options)
    {
        var cases = context.UnionCases.Select(c => c.CaseType).ToArray();
        var proxies = cases.Where(t => t.GetCustomAttribute<JsProxyTypeAttribute>() is not null).ToArray();
        var tagged = cases
            .SelectMany(t => t.GetCustomAttributes<JsDiscriminatorAttribute>().Select(a => (a.Property, a.Value, Type: t)))
            .ToArray();
        var tagProperties = tagged.Select(t => t.Property).Distinct().ToArray();

        return (ref Utf8JsonReader reader) =>
        {
            var kind = reader.TokenType;
            if (kind == JsonTokenType.StartObject)
            {
                // Scan the top-level properties of a copy; the serializer re-reads from the original.
                var scan = reader;
                string? jsType = null;
                bool isHandle = false;
                var seenTags = new Dictionary<string, string>();
                scan.Read();
                while (scan.TokenType == JsonTokenType.PropertyName)
                {
                    var name = scan.GetString()!;
                    scan.Read();
                    if (name == "__jsObjectId") isHandle = true;
                    else if (name == "__jsType" && scan.TokenType == JsonTokenType.String) jsType = scan.GetString();
                    else if (scan.TokenType == JsonTokenType.String && tagProperties.Contains(name)) seenTags[name] = scan.GetString()!;
                    scan.Skip();
                    scan.Read();
                }

                if (isHandle)
                {
                    if (proxies.Length == 1) return proxies[0];
                    var match = proxies.FirstOrDefault(p => p.GetCustomAttribute<JsProxyTypeAttribute>()!.JsType == jsType);
                    return match ?? throw new JsonException($"JS handle of type '{jsType}' matches no case of {context.DeclaringType.Name}.");
                }

                foreach (var (prop, value, type) in tagged)
                    if (seenTags.TryGetValue(prop, out var v) && v == value) return type;
            }

            var byToken = cases.Where(t => Accepts(t, kind)).ToArray();
            if (byToken.Length == 1) return byToken[0];
            throw new JsonException(
                $"Cannot choose a case of {context.DeclaringType.Name} for a JSON {kind}; candidates: {string.Join(", ", byToken.Select(t => t.Name))}.");
        };
    }

    private static bool Accepts(Type t, JsonTokenType kind)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return kind switch
        {
            JsonTokenType.String => t == typeof(string) || t.IsEnum,
            JsonTokenType.Number => t.IsPrimitive && t != typeof(bool) && t != typeof(char) || t == typeof(decimal),
            JsonTokenType.True or JsonTokenType.False => t == typeof(bool),
            JsonTokenType.StartArray => t.IsArray || (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t)),
            JsonTokenType.StartObject => !t.IsPrimitive && t != typeof(string) && !t.IsArray && !t.IsEnum,
            _ => false,
        };
    }
}
