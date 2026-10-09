namespace Interoper.Runtime.Tests;

public class JsJsonTests
{
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

    [Fact] // R1: where a value can't be omitted (array elements, arguments), undefined is {"$u":1}
    public void Undefined_value_is_the_marker()
    {
        Assert.Equal("""{"$u":1}""", Json.Write(default(ColorInput), JsJson.WriteValue));
        Assert.Equal("""{"$u":1}""", Json.Write(default(Opt<double>), (w, v) => JsJson.WriteValue(w, v, Json.WriteDouble)));
        Assert.Equal("null", Json.Write((ColorInput)null, JsJson.WriteValue));
        Assert.Equal("5", Json.Write((Opt<double>)5, (w, v) => JsJson.WriteValue(w, v, Json.WriteDouble)));
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
}
