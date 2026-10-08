using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace WasmTransport;

[SupportedOSPlatform("browser")]
public static partial class JsBind
{
    [JSImport("create", "jsbind")] public static partial int Create(string className, string propsJson);
    [JSImport("getNumber", "jsbind")] public static partial double GetNumber(int handle, string name);
    [JSImport("setNumber", "jsbind")] public static partial void SetNumber(int handle, string name, double value);
    [JSImport("getJson", "jsbind")] public static partial string GetJson(int handle, string names);
    [JSImport("fireEvent", "jsbind")] public static partial Task<string> FireEvent(int handle, string payload);
}

[SupportedOSPlatform("browser")]
public static partial class JsCallbacks
{
    public static string? Last;
    [JSExport] public static string OnEvent(int handle, string payload) { Last = $"{handle}:{payload}"; return "ack"; }
}

// What a generated "live object" looks like when the transport is synchronous.
[SupportedOSPlatform("browser")]
public sealed class LiveMapView(int handle)
{
    public double Zoom { get => JsBind.GetNumber(handle, "zoom"); set => JsBind.SetNumber(handle, "zoom", value); }
    public double Rotation { get => JsBind.GetNumber(handle, "rotation"); set => JsBind.SetNumber(handle, "rotation", value); }
}
