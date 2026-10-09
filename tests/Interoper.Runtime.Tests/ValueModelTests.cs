// Ported from the value-model experiment (design §8, 7d48a23:spikes/value-model).
//
// Compile-time facts these tests rely on but can't assert at run time (checked by hand when writing them):
// - Removing the `Undefined` arm from a switch below warns CS8509; removing the `null` arm warns CS8655.
//   The `null` arm is only demanded because the bottom case is the nullable `Undefined?` (design §8).
// - `ColorInput c = null;` compiles only through `implicit operator ColorInput(Undefined? value)`
//   (without it, CS0037); IJsUnion<T> makes every generated union declare it.
// - Removing a subclass arm from a switch over the `closed` Shape warns CS8509, through `Opt<Shape>` too.
// TreatWarningsAsErrors turns each of those into a build break.
// R5 (collapse) and R6 (flatten) are generator rules, tested in phase 2.
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
    public void Closed_live_hierarchy_is_exhaustive()
    {
        Assert.Equal("line", Kind(new Line(7)));
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
    public void Opt_of_closed_base_is_exhaustive_over_subclasses_undefined_and_null()
    {
        Assert.Equal("polygon", ShapeKind(new Polygon(3)));
        Assert.Equal("undefined", ShapeKind(default));
        Assert.Equal("null", ShapeKind(null));
    }
}
