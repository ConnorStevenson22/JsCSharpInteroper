// Verifies the TypeScript -> C# 15 mappings in Generated/Sample.cs against the real
// Microsoft.JSInterop serializer (.NET 11). Each check prints the wire JSON it observed.
using System.Text.Json;
using Microsoft.JSInterop;
using UnionInterop;
using UnionInterop.Generated.Sample;
using UnionInterop.Runtime;

var js = new FakeJsRuntime();
int failures = 0;

async Task Check(string name, Func<Task<string>> body, bool expectThrow = false)
{
    try
    {
        var observed = await body();
        Console.WriteLine($"{(expectThrow ? "FAIL" : "PASS")}  {name}\n        {observed}");
        if (expectThrow) failures++;
    }
    catch (Exception ex) when (expectThrow)
    {
        Console.WriteLine($"PASS  {name}\n        throws {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}\n        {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        failures++;
    }
}

void Expect(bool condition, string what)
{
    if (!condition) throw new Exception("expected " + what);
}

// Send `arg` to JS, return the args JSON Blazor produced.
async Task<string> Send(object? arg)
{
    js.Reply = (_, _) => "null"; // what blazor.*.js sends back for a void call
    await js.InvokeVoidAsync("probe", arg);
    return js.Calls[^1].ArgsJson!;
}

// Have "JS" return `json`, deserialized as T by Blazor's interop serializer.
async Task<T> Receive<T>(string json)
{
    js.Reply = (_, _) => json;
    return await js.InvokeAsync<T>("probe");
}

// 1. literal union -> enum
await Check("literal union -> enum (out)", async () =>
{
    var json = await Send(MarkerStyle.Diamond);
    Expect(json == """["diamond"]""", "[\"diamond\"]");
    return json;
});
await Check("literal union -> enum (in)", async () =>
{
    var v = await Receive<MarkerStyle>("\"triangle\"");
    Expect(v == MarkerStyle.Triangle, "Triangle");
    return v.ToString();
});

// 2. primitive union
await Check("string | number (out, both cases)", async () =>
{
    var a = await Send((IdOrName)"A1");
    var b = await Send((IdOrName)42.0);
    Expect(a == """["A1"]""" && b == """[42]""", "raw values, no wrapper");
    return a + "  " + b;
});
await Check("string | number (in, both cases)", async () =>
{
    var a = await Receive<IdOrName>("\"A1\"");
    var b = await Receive<IdOrName>("42");
    var describe = (IdOrName c) => c switch
    {
        string s => $"string {s}",
        double d => $"double {d}",
    }; // exhaustive: no default arm, no warning
    Expect(a.Value is "A1" && b.Value is 42.0, "string then double");
    return describe(a) + ", " + describe(b);
});

// 3. mixed union: string | number[] | RGBA
await Check("ColorInput (in): string / array / object by token kind", async () =>
{
    var s = await Receive<ColorInput>("\"#ff0000\"");
    var arr = await Receive<ColorInput>("[255,0,0,0.5]");
    var obj = await Receive<ColorInput>("""{"r":255,"g":0,"b":0}""");
    Expect(s.Value is string && arr.Value is double[] { Length: 4 } && obj.Value is Rgba { R: 255 }, "string, double[4], Rgba");
    return $"{s.Value} | double[{((double[])arr.Value!).Length}] | {obj.Value}";
});

// 4. discriminated union with our value-aware classifier
await Check("shape data bag (out) keeps the type tag", async () =>
{
    ShapeData g = new PointData { X = 1, Y = 2 };
    var json = await Send(g);
    Expect(json.Contains("\"type\":\"point\""), "type tag");
    return json;
});
await Check("shape data bag (in) chosen by tag value", async () =>
{
    var g = await Receive<ShapeData>("""{"type":"line","points":[[0,0],[1,1]]}""");
    var e = await Receive<ShapeData>("""{"xmin":0,"ymin":0,"xmax":1,"ymax":1,"type":"box"}""");
    Expect(g.Value is LineData && e.Value is BoxData, "Line then Box");
    return $"{g.Value!.GetType().Name}, {e.Value!.GetType().Name}";
});

// 5. untagged object union with the built-in structural classifier
await Check("ScreenPoint | ScreenRect: {x,y} -> ScreenPoint", async () =>
{
    var p = await Receive<HitTarget>("""{"x":1,"y":2}""");
    Expect(p.Value is ScreenPoint, "ScreenPoint");
    return p.Value!.ToString()!;
});
await Check("ScreenPoint | ScreenRect: {x,y,width,height} -> ScreenRect (narrowed by 'width')", async () =>
{
    var r = await Receive<HitTarget>("""{"x":1,"y":2,"width":3,"height":4}""");
    Expect(r.Value is ScreenRect, "ScreenRect");
    return r.Value!.ToString()!;
});
await Check("...but if the distinguishing members are optional (TS `width?`), {x,y} is ambiguous", async () =>
{
    var r = await Receive<LooseHitTarget>("""{"x":1,"y":2}""");
    return r.Value!.ToString()!;
}, expectThrow: true);

// 6. JS handles inside unions
await Check("Blazor's own IJSObjectReference reader rejects extra properties", async () =>
{
    var r = await Receive<IJSObjectReference>("""{"__jsObjectId":3,"__jsType":"demo.Point"}""");
    return r.ToString()!;
}, expectThrow: true);
await Check("Shape (Point | Line proxies): handle routed by __jsType", async () =>
{
    var feature = Feature.Wrap(await Receive<IJSObjectReference>("""{"__jsObjectId":7}"""));
    js.Reply = (_, _) => """{"__jsObjectId":8,"__jsType":"demo.Line"}""";
    Shape? shape = await feature.GetShapeAsync();
    // Patterns see through both Nullable<Shape> and the union. Note `shape.Value` would be
    // Nullable<T>.Value (the Shape), not the union's content - an easy trap with struct unions.
    Expect(shape is Line, "Line proxy");
    return shape is Line line ? $"Line wrapping {line.Reference.GetType().Name}" : "?";
});
await Check("setter accepts instance OR property bag", async () =>
{
    var feature = Feature.Wrap(await Receive<IJSObjectReference>("""{"__jsObjectId":7}"""));
    var point = Point.Wrap(await Receive<IJSObjectReference>("""{"__jsObjectId":9}"""));
    js.Reply = (_, _) => "null";
    await feature.SetShapeAsync(point);
    var a = js.Calls[^1].ArgsJson!;
    await feature.SetShapeAsync(new LineData { Points = [[0, 0], [5, 5]] });
    var b = js.Calls[^1].ArgsJson!;
    Expect(a.Contains("\"__jsObjectId\":9") && b.Contains("\"type\":\"line\""), "handle, then tagged bag");
    return a + "  " + b;
});

// 7. undefined vs null vs value
await Check("optional + nullable: undefined omitted, null kept, value written", async () =>
{
    var json = await Send(new FeatureData
    {
        Shape = (ShapeData)new PointData { X = 1, Y = 2 },
        Color = JsOptional<ColorInput>.Null,
        // Title left undefined
    });
    Expect(json.Contains("\"color\":null") && !json.Contains("title"), "color:null, no title");
    return json;
});
await Check("optional + nullable (in): absent / null / value", async () =>
{
    var p = await Receive<FeatureData>("""{"color":null,"title":"Hi"}""");
    Expect(p.Shape.IsUndefined && p.Color.IsNull && p.Title.Value == "Hi", "undefined, null, value");
    return $"shape={p.Shape}, color={p.Color}, title={p.Title}";
});

// 8. Plain System.Text.Json (no Blazor options) for comparison: default token classification.
await Check("plain STJ: union(string, double) without any attribute", async () =>
{
    var json = JsonSerializer.Serialize<IdOrName>(7.5);
    var back = JsonSerializer.Deserialize<IdOrName>(json);
    Expect(back.Value is 7.5, "7.5");
    return await Task.FromResult(json);
});
await Check("plain STJ + JsonSerializerOptions.Web: string|number becomes ambiguous", async () =>
{
    var back = JsonSerializer.Deserialize<IdOrName>("\"A1\"", JsonSerializerOptions.Web);
    return await Task.FromResult(back.Value!.ToString()!);
}, expectThrow: true);

// 9. Boxing: compiler-generated union vs. generator-emitted non-boxing union.
await Check("non-boxing custom union round-trips and allocates nothing to match", async () =>
{
    StringOrNumber n = 4.5;
    var json = JsonSerializer.Serialize(n);
    var back = JsonSerializer.Deserialize<StringOrNumber>("\"id-7\"");
    Expect(json == "4.5" && back is string, "4.5 and a string case");

    double sum = 0;
    long t0 = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 100_000; i++) { IdOrName u = (double)i; if (u is double d) sum += d; }
    long boxed = GC.GetAllocatedBytesForCurrentThread() - t0;
    t0 = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 100_000; i++) { StringOrNumber u = (double)i; if (u is double d) sum += d; }
    long unboxed = GC.GetAllocatedBytesForCurrentThread() - t0;
    Expect(unboxed == 0, "0 bytes");
    return await Task.FromResult($"100k create+match of a double: `union` {boxed:N0} B, custom {unboxed:N0} B");
});

Console.WriteLine(failures == 0 ? "\nAll checks behaved as documented." : $"\n{failures} check(s) failed.");
return failures;
