// Compile-time facts these tests rely on but can't assert at run time (design §8):
// - Removing the `Undefined` arm from a switch below warns CS8509; removing the `null` arm warns CS8655.
//   The `null` arm is only demanded because the bottom case is the nullable `Undefined?` (design §8).
// - `ColorInput c = null;` compiles only through `implicit operator ColorInput(Undefined? value)`
//   (without it, CS0037); IJsUnion<T> makes every generated union declare it.
// - Removing a subclass arm from a switch over the `closed` Shape warns CS8509, through `Opt<Shape>` too.
// TreatWarningsAsErrors turns each of those into a build break.
// The types come from tests/GoldenSlice.
using GoldenSlice.Async;

namespace Interoper.Runtime.Tests;

public class ValueModelTests
{
    [Fact] // R1
    public void Undefined_is_a_singleton_users_cannot_construct()
    {
        Assert.Empty(typeof(Undefined).GetConstructors());
        Assert.Same(Undefined.Value, Undefined.Value);
        Assert.Equal("undefined", Undefined.Value.ToString());
    }

    [Fact] // R2, R3
    public void Unset_member_is_undefined_null_literal_is_js_null_value_is_value()
    {
        var withValue = new StrokeStyle { Color = "red" };
        var withNull = new StrokeStyle { Color = null };

        Assert.True(withValue.Color.TryGetValue(out string? color));
        Assert.Equal("red", color);
        Assert.True(withNull.Color.IsNull);
        Assert.True(withNull.Width.IsUndefined);
        Assert.Same(Undefined.Value, default(ColorInput).Value);
    }

    [Fact] // R1, R2
    public void Explicit_undefined_via_the_singleton()
    {
        var style = new StrokeStyle { Color = "red" } with { Color = Undefined.Value };

        Assert.True(style.Color.IsUndefined);
        Assert.Equal(default, style.Color);
    }

    private static string Describe(ColorInput color) => color switch
    {
        string s => $"css {s}",
        double[] rgba => $"array[{rgba.Length}]",
        Rgba rgba => $"rgba r={rgba.R}",
        Undefined => "undefined",
        null => "null",
    };

    [Fact] // R2
    public void Exhaustive_switch_over_value_undefined_and_null()
    {
        Assert.Equal("css red", Describe("red"));
        Assert.Equal("array[4]", Describe(new double[] { 1, 2, 3, 4 }));
        Assert.Equal("rgba r=255", Describe(new Rgba { R = 255, G = 0, B = 0 }));
        Assert.Equal("undefined", Describe(default));
        Assert.Equal("null", Describe(null));
    }

    private static string DescribeSize(IconStyleSize size) => size switch
    {
        double px => $"{px}px",
        string css => css,
        Undefined => "undefined",
        null => "null",
    };

    [Fact] // R2: a number case is kept unboxed, and switches and compares like any other
    public void A_number_case_switches_and_compares_like_any_other()
    {
        Assert.Equal("12px", DescribeSize(12));
        Assert.Equal("2em", DescribeSize("2em"));
        Assert.Equal("undefined", DescribeSize(default));
        Assert.Equal("null", DescribeSize(null));
        Assert.Equal<IconStyleSize>(12, 12.0);
        Assert.NotEqual<IconStyleSize>(12, "12");
        Assert.NotEqual<IconStyleSize>(0, default); // undefined isn't 0
        Assert.True(((MeasureDistanceUnit)LengthUnit.Kilometers).TryGetValue(out LengthUnit unit) && unit == LengthUnit.Kilometers);
        Assert.False(((MeasureDistanceUnit)0).TryGetValue(out LengthUnit _)); // 0.0 is the double case
    }

    [Fact] // R2, R19 step 4: a number, string or enum case is told apart by its JSON token
    public void Value_type_cases_are_read_by_their_JSON_token()
    {
        Assert.Equal("""{"type":"icon","name":"pin-1","size":12}""", Json.Write(new IconStyle { Name = IconName.Pin1, Size = 12 }, IconStyle.Write));
        Assert.Equal<IconStyleSize>(12, Json.Read("12", JsJson.ReadUnion<IconStyleSize>));
        Assert.Equal<IconStyleSize>("2em", Json.Read("\"2em\"", JsJson.ReadUnion<IconStyleSize>));
        Assert.True(Json.Read("null", JsJson.ReadUnion<IconStyleSize>).IsNull);
        Assert.Equal<StrokeStyleDash>(StrokeStyleDashLiteral.Dotted, Json.Read("\"dotted\"", JsJson.ReadUnion<StrokeStyleDash>));
        Assert.Throws<System.Text.Json.JsonException>(() => Json.Read("true", JsJson.ReadUnion<IconStyleSize>));
    }

    private static string Size(Opt<double> size) => size switch
    {
        double d => $"{d}px",
        Undefined => "default size",
        null => "no size",
    };

    [Fact] // R3
    public void Opt_has_the_same_three_states_for_plain_types()
    {
        Assert.Equal("12px", Size(12));
        Assert.Equal("default size", Size(default));
        Assert.Equal("no size", Size(null));
    }

    [Fact] // R3
    public void Opt_TryGetValue_infers_its_type_and_is_patterns_read_it()
    {
        Opt<double> size = 12;
        Assert.True(size.TryGetValue(out var value) && value == 12);
        Assert.False(((Opt<double>)default).TryGetValue(out _));
        Assert.True(size is double d && d == 12);
        Assert.True(default(Opt<double>) is Undefined);
    }

    [Fact] // R2, R3, R20
    public void Wire_format_omits_undefined_and_keeps_null()
    {
        var style = new StrokeStyle { Color = new double[] { 255, 0, 0, 1 } };

        Assert.Equal("""{"type":"stroke","color":[255,0,0,1]}""", Json.Write(style, StrokeStyle.Write));
        Assert.Equal("""{"type":"stroke","color":null,"width":2}""", Json.Write(new StrokeStyle { Color = null, Width = 2 }, StrokeStyle.Write));
        Assert.Equal("""{"type":"stroke"}""", Json.Write(new StrokeStyle(), StrokeStyle.Write));
    }

    private static string Kind(Shape shape) => shape switch
    {
        Point => "point",
        Line => "line",
        Polygon => "polygon",
    };

    [Fact] // R4
    public async Task Closed_live_hierarchy_is_exhaustive()
    {
        await using var realm = await Realm();
        await using var scope = new JsScope(realm);
        Assert.Equal("line", Kind(await Line.CreateAsync(scope, new LineSnapshot())));
    }

    // A nullable getter such as `get shape(): Point | Line | Polygon | null | undefined` maps to Opt<Shape> (R3, R4).
    private static string ShapeKind(Opt<Shape> shape) => shape switch
    {
        Point => "point",
        Line => "line",
        Polygon => "polygon",
        Undefined => "undefined",
        null => "null",
    };

    [Fact] // R3, R4
    public async Task Opt_of_closed_base_is_exhaustive_over_subclasses_undefined_and_null()
    {
        await using var realm = await Realm();
        await using var scope = new JsScope(realm);
        Assert.Equal("polygon", ShapeKind(await Polygon.CreateAsync(scope, new PolygonSnapshot())));
        Assert.Equal("undefined", ShapeKind(default));
        Assert.Equal("null", ShapeKind(null));
    }

    private static async Task<JsRealm> Realm()
    {
        var realm = new JsRealm(() => Task.FromResult<IJsTransport>(InMemoryDemoMaps.Create()), DemoMaps.Classes);
        await realm.StartAsync();
        return realm;
    }
}
