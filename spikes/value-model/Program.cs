using System.Text.Json;
using System.Text.Json.Serialization;
using ValueModel;

int failures = 0;
void Check(string name, bool ok, string observed)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}\n        {observed}");
    if (!ok) failures++;
}

// The three JS states, with no initializers anywhere.
var bag = new MarkerProperties { Color = "red", Size = null };
Check("unset member is undefined, `null` literal is JS null, value is value",
    bag.Style.IsUndefined && bag.Size.IsNull && bag.Color.Value is "red",
    $"color={bag.Color} size={bag.Size} style={bag.Style}");

Check("explicit undefined via the singleton",
    (bag with { Color = Undefined.Value }).Color.Value is Undefined,
    $"{(bag with { Color = Undefined.Value }).Color}");

// Exhaustive pattern matching over all JS states. Removing the `null` arm gives CS8655,
// removing `Undefined` gives CS8509 (checked when building this file by hand).
static string Describe(ColorInput c) => c switch
{
    string s => $"css {s}",
    double[] rgba => $"array[{rgba.Length}]",
    Rgba r => $"rgba r={r.R}",
    Undefined => "undefined",
    null => "null",
};
Check("exhaustive switch: value / undefined / null",
    Describe("red") == "css red" && Describe(default) == "undefined" && Describe(null) == "null",
    $"{Describe(new double[] { 1, 2, 3, 4 })} | {Describe(default)} | {Describe(null)} | {Describe(new Rgba { R = 255 })}");

static string Size(Opt<double> s) => s switch { double d => $"{d}px", Undefined => "default size", null => "no size" };
Check("Opt<T>: same three states for plain types, no boxing",
    Size(12) == "12px" && Size(default) == "default size" && Size(null) == "no size",
    $"{Size(12)} | {Size(default)} | {Size(null)}");

// Wire format with nothing but attributes (the generator would emit its own writers).
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
};
var json = JsonSerializer.Serialize(new MarkerProperties { Color = new double[] { 255, 0, 0, 1 }, Size = null }, options);
Check("serialization: undefined omitted, null kept",
    json == """{"color":[255,0,0,1],"size":null}""", json);

// Closed live-object hierarchy: exhaustive without a default arm.
static string Kind(Geometry g) => g switch { Point => "point", Polyline => "polyline", Polygon => "polygon" };
Check("closed proxy hierarchy is exhaustive", Kind(new Polyline(7)) == "polyline", Kind(new Polyline(7)));

// A nullable live getter, `get geometry(): GeometryUnion | null | undefined`, becomes Opt<Geometry>.
// Exhaustiveness flows through Opt<T> into the closed hierarchy (omit Polygon -> CS8509).
static string GeometryKind(Opt<Geometry> g) => g switch
{
    Point => "point", Polyline => "polyline", Polygon => "polygon",
    Undefined => "undefined", null => "null",
};
Check("Opt<closed base>: subclass patterns + undefined + null, exhaustive",
    GeometryKind(new Polygon(3)) == "polygon" && GeometryKind(default) == "undefined" && GeometryKind(null) == "null",
    $"{GeometryKind(new Polygon(3))} | {GeometryKind(default)} | {GeometryKind(null)}");

Console.WriteLine(failures == 0 ? "\nAll value-model checks passed." : $"\n{failures} failed.");
return failures;
