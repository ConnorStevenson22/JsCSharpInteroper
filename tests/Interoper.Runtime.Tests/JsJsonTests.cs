using System.Text.Json;
using GoldenSlice.Async;

namespace Interoper.Runtime.Tests;

public class JsJsonTests
{
    /// <summary>A handle, as JS sends an instance of a class outside the selection: a case a union dropped (R2).</summary>
    private const string Handle = """{"$h":3,"$t":0}""";

    [Fact] // R2: a reader given a handle where it expects a value throws JsonException, not InvalidOperationException
    public void A_handle_where_a_value_is_expected_is_a_JsonException()
    {
        Assert.Throws<JsonException>(() => Json.Read(Handle, JsJson.ReadNumber));
        Assert.Throws<JsonException>(() => Json.Read(Handle, JsJson.ReadString));
        Assert.Throws<JsonException>(() => Json.Read(Handle, JsJson.ReadBoolean));
        Assert.Throws<JsonException>(() => Json.Read(Handle, BasemapNames.Read)); // R9
        Assert.Throws<JsonException>(() => Json.Read(Handle, Cursor.Read)); // R10
        Assert.Throws<JsonException>(() => Json.Read(Handle, JsJson.ReadUnion<Style>)); // R19 step 2
        Assert.Throws<JsonException>(() => Json.Read(Handle, JsJson.ReadUnion<FlyToTarget>)); // R19 step 3
    }

    private sealed record Saved(PointSnapshot Center, ShapeSnapshot Shape, StrokeStyle Style, MapViewSnapshot View);

    [Fact] // R15, R16, R18: System.Text.Json uses a record's own wire format, so app types can hold records and snapshots; an entity's that takes a bag has $c too
    public void Records_and_snapshots_serialize_with_System_Text_Json()
    {
        var saved = new Saved(new PointSnapshot { X = 1, Y = 2 }, new PointSnapshot { X = 3 }, new StrokeStyle { Width = 2 }, new MapViewSnapshot { Zoom = 4 });
        var json = JsonSerializer.Serialize(saved);
        Assert.Equal($$$"""{"Center":{"$c":{{{DemoMaps.PointId}}},"type":"point","x":1,"y":2},"Shape":{"$c":{{{DemoMaps.PointId}}},"type":"point","x":3},"Style":{"type":"stroke","width":2},"View":{"$c":{{{DemoMaps.MapViewId}}},"zoom":4}}""", json);
        Assert.Equal(saved, JsonSerializer.Deserialize<Saved>(json));
    }

    [Theory] // R2, R3, R20: undefined omitted, null kept, value written, discriminant always written
    [InlineData("""{"type":"stroke"}""")]
    [InlineData("""{"type":"stroke","color":null}""")]
    [InlineData("""{"type":"stroke","color":"red","width":2}""")]
    [InlineData("""{"type":"stroke","color":[1,2,3]}""")]
    [InlineData("""{"type":"stroke","color":{"r":1,"g":2,"b":3}}""")]
    [InlineData("""{"type":"stroke","color":{"r":1,"g":2,"b":3,"a":0.5},"width":1}""")]
    public void Bag_round_trips(string json)
    {
        var style = Json.Read(json, StrokeStyle.Read);

        Assert.Equal(json, Json.Write(style, StrokeStyle.Write));
    }

    [Fact] // R2, R3, R19 step 4
    public void Reading_restores_each_state()
    {
        var omitted = Json.Read("""{"type":"stroke"}""", StrokeStyle.Read);
        var nulled = Json.Read("""{"type":"stroke","color":null}""", StrokeStyle.Read);
        var rgba = Json.Read("""{"type":"stroke","color":{"r":1,"g":2,"b":3}}""", StrokeStyle.Read);

        Assert.True(omitted.Color.IsUndefined);
        Assert.True(omitted.Width.IsUndefined);
        Assert.True(nulled.Color.IsNull);
        Assert.True(rgba.Color.TryGetValue(out Rgba? value));
        Assert.Equal(new Rgba { R = 1, G = 2, B = 3 }, value);
    }

    [Fact] // §2: any is its JSON, read with a typed reader; the scope that reads it owns its handles
    public async Task Any_is_its_json_and_its_handles_are_the_scopes()
    {
        Assert.Equal("""{"a":[1,"b"]}""", Json.Write(JsAny.Parse("""{"a":[1,"b"]}"""), JsAny.Write));
        Assert.Equal(3, JsAny.From(3.0, JsJson.WriteNumber).Read(JsJson.ReadNumber));
        Assert.Equal(JsAny.Parse("""{"a":1,"b":[2]}"""), JsAny.Parse("""{ "b": [2], "a": 1 }""")); // by its JSON
        Assert.NotEqual(JsAny.Parse("[1]"), JsAny.Parse("[2]"));

        var transport = InMemoryDemoMaps.Create();
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(transport), DemoMaps.Classes);
        await realm.StartAsync();
        var marker = Create(); // {"$h":1,"$t":6}
        var scope = new JsScope(realm);
        var any = Json.Read($$"""{"items":[{{marker}}]}""", (ref r) => JsAny.Read(ref r, scope));
        Assert.Equal(marker, any.Json.GetProperty("items")[0].GetRawText());
        Assert.Equal(1, transport.Census());
        await scope.DisposeAsync();
        Assert.Equal(0, transport.Census()); // released with the scope

        // Read after its scope is disposed, as when the call was in flight: every handle in it is released.
        var (first, second) = (Create(), Create());
        Assert.Equal(2, transport.Census());
        Assert.Throws<ObjectDisposedException>(() => Json.Read($$"""[{{first}},{"m":{{second}}}]""", (ref r) => JsAny.Read(ref r, scope)));
        Assert.Equal(0, transport.Census());

        string Create() => transport.Invoke($$"""{"op":"create","t":{{DemoMaps.MarkerId}},"args":[]}""")[5..^1];
    }

    [Fact] // R17: a snapshot's any is data, read with no scope, so it may hold no handle
    public void A_snapshots_any_is_data_without_handles()
    {
        Assert.Equal(JsAny.Parse("""{"a":[1]}"""), Json.Read("""{"a":[1]}""", JsAny.ReadData));
        Assert.Throws<JsonException>(() => Json.Read("""{"a":[{"$h":1,"$t":6}]}""", JsAny.ReadData));
    }

    [Fact] // R21: a missing callback is JS null, or undefined when its type has no null
    public void A_callback_is_its_id_and_a_missing_one_null_or_undefined()
    {
        Assert.Equal("""{"$f":3}""", Json.Write<int?>(3, (w, id) => JsJson.WriteCallback(w, id)));
        Assert.Equal("null", Json.Write<int?>(null, (w, id) => JsJson.WriteCallback(w, id)));
        Assert.Equal("""{"$u":1}""", Json.Write<int?>(null, (w, id) => JsJson.WriteCallback(w, id, undefined: true)));
    }

    [Fact] // R1: where a value can't be omitted (array elements, arguments), undefined is {"$u":1}
    public void Undefined_value_is_the_marker()
    {
        Assert.Equal("""{"$u":1}""", Json.Write(default(ColorInput), JsJson.WriteValue));
        Assert.Equal("""{"$u":1}""", Json.Write(default(Opt<double>), (w, v) => JsJson.WriteValue(w, v, JsJson.WriteNumber)));
        Assert.Equal("null", Json.Write((ColorInput)null, JsJson.WriteValue));
        Assert.Equal("5", Json.Write((Opt<double>)5, (w, v) => JsJson.WriteValue(w, v, JsJson.WriteNumber)));
    }

    [Fact] // R1: the marker is read back as undefined, and the reader moves past it
    public void Marker_reads_as_undefined()
    {
        var style = Json.Read("""{"type":"stroke","color":{"$u":1},"width":{"$u":1}}""", StrokeStyle.Read);

        Assert.True(style.Color.IsUndefined);
        Assert.True(style.Width.IsUndefined);
        Assert.True(Json.Read("""{"$u":1}""", JsJson.ReadUnion<ColorInput>).IsUndefined);
        Assert.True(Json.Read("""{"r":1,"g":2,"b":3}""", JsJson.ReadUnion<ColorInput>).TryGetValue(out Rgba? _));
    }

    [Fact] // R5: tuples and arrays of them
    public void Arrays_round_trip()
    {
        double[][] pairs = [[0, 1], [2, 3]];
        var json = Json.Write(pairs, (w, v) => JsJson.WriteArray(w, v, (w1, v1) => JsJson.WriteArray(w1, v1, JsJson.WriteNumber)));

        Assert.Equal("[[0,1],[2,3]]", json);
        Assert.Equal(pairs, Json.Read(json, (ref r) => JsJson.ReadArray(ref r, (ref r1) => JsJson.ReadArray(ref r1, JsJson.ReadNumber))));
    }

    [Fact] // R19 steps 2 and 3: a classifier peeks at the object's own members, and the reader stays where it was
    public void Peeks_leave_the_reader_on_the_object()
    {
        var reader = new Utf8JsonReader("""{"a":{"type":"x"},"type":"point","b":1}"""u8);
        reader.Read();

        Assert.Equal("point", JsJson.PeekString(reader, "type"u8));
        Assert.Null(JsJson.PeekString(reader, "b"u8)); // not a string
        Assert.True(JsJson.HasProperty(reader, "b"u8));
        Assert.False(JsJson.HasProperty(reader, "x"u8));
        Assert.Equal(JsonTokenType.StartObject, reader.TokenType);
    }

    [Theory] // R6, R19: FlyToTarget's flattened cases, by token kind (step 4) or by a required member (step 3)
    [InlineData("""[1,2]""", typeof(double[]))]
    [InlineData("""{"$c":4,"type":"polygon","vertices":[[0,0]]}""", typeof(PolygonSnapshot))]
    [InlineData("""{"minX":0,"minY":0,"maxX":1,"maxY":1}""", typeof(Bounds))]
    [InlineData("""{"center":[1,2],"zoom":3}""", typeof(CenterAndZoom))]
    public void Union_cases_are_classified(string json, Type expected)
    {
        Assert.IsType(expected, Json.Read(json, JsJson.ReadUnion<FlyToTarget>).Value);
    }

    [Fact] // R16, R20: a value-like snapshot is written as an instance of its class, keeping its discriminant
    public void Value_like_snapshots_carry_their_class_id()
    {
        Assert.Equal("""{"$c":2,"type":"point","x":1}""", Json.Write((FlyToTarget)new PointSnapshot { X = 1 }, JsJson.WriteValue));
        Assert.Equal("""{"x":1}""", Json.Write(new PointSnapshot { X = 1 }, (w, v) =>
        {
            w.WriteStartObject();
            PointSnapshot.WriteMembers(w, v); // a bag or a patch
            w.WriteEndObject();
        }));
    }
}
