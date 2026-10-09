namespace Interoper.Runtime.Tests;

public class JsJsonTests
{
    [Theory] // R2, R3: undefined omitted, null kept, value written
    [InlineData("""{}""")]
    [InlineData("""{"color":null,"width":null}""")]
    [InlineData("""{"color":"red","width":2}""")]
    [InlineData("""{"color":[1,2,3]}""")]
    [InlineData("""{"color":{"r":1,"g":2,"b":3}}""")]
    [InlineData("""{"color":{"r":1,"g":2,"b":3,"a":null}}""")]
    [InlineData("""{"color":{"r":1,"g":2,"b":3,"a":0.5},"width":1}""")]
    public void Bag_round_trips(string json)
    {
        var style = Json.Read(json, StrokeStyle.Read);

        Assert.Equal(json, Json.Write(style, StrokeStyle.Write));
    }

    [Fact] // R2, R3, R19 step 4
    public void Reading_restores_each_state()
    {
        var omitted = Json.Read("{}", StrokeStyle.Read);
        var nulls = Json.Read("""{"color":null,"width":null}""", StrokeStyle.Read);
        var rgba = Json.Read("""{"color":{"r":1,"g":2,"b":3}}""", StrokeStyle.Read);

        Assert.True(omitted.Color.IsUndefined);
        Assert.True(omitted.Width.IsUndefined);
        Assert.True(nulls.Color.IsNull);
        Assert.True(nulls.Width.IsNull);
        Assert.True(rgba.Color.TryGetValue(out Rgba value));
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
}
