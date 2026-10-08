using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace HostsSpike.Client.Runtime;

/// <summary>WebAssembly: synchronous [JSImport] calls wrapped in already-completed ValueTasks.</summary>
[SupportedOSPlatform("browser")]
public sealed partial class JsImportTransport : IJsTransport
{
    private JsImportTransport() { }

    public string Name => "WebAssembly ([JSImport])";

    public static async Task<JsImportTransport> CreateAsync()
    {
        await JSHost.ImportAsync("interoper", "/interoper.js");
        await Native.UseWasmExports(typeof(JsImportTransport).Assembly.GetName().Name + ".dll");
        return new JsImportTransport();
    }

    public ValueTask<int> CreateAsync(string jsClass, string propertiesJson) => new(Native.Create(jsClass, propertiesJson));
    public ValueTask<double> GetNumberAsync(int handle, string property) => new(Native.GetNumber(handle, property));
    public ValueTask<string> SnapshotAsync(int handle, string properties) => new(Native.Snapshot(handle, properties));

    public ValueTask SetNumberAsync(int handle, string property, double value)
    {
        Native.SetNumber(handle, property, value);
        return ValueTask.CompletedTask;
    }

    public ValueTask SubscribeAsync(int handle, string eventName, int callbackId)
    {
        Native.Subscribe(handle, eventName, callbackId);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(int handle)
    {
        Native.Release(handle);
        return ValueTask.CompletedTask;
    }

    public ValueTask RaiseAsync(int handle, string eventName, string payloadJson)
    {
        Native.Raise(handle, eventName, payloadJson);
        return ValueTask.CompletedTask;
    }

    private static partial class Native
    {
        [JSImport("useWasmExports", "interoper")] internal static partial Task UseWasmExports(string assemblyName);
        [JSImport("create", "interoper")] internal static partial int Create(string jsClass, string propertiesJson);
        [JSImport("getNumber", "interoper")] internal static partial double GetNumber(int handle, string property);
        [JSImport("setNumber", "interoper")] internal static partial void SetNumber(int handle, string property, double value);
        [JSImport("snapshot", "interoper")] internal static partial string Snapshot(int handle, string properties);
        [JSImport("subscribe", "interoper")] internal static partial void Subscribe(int handle, string eventName, int callbackId);
        [JSImport("release", "interoper")] internal static partial void Release(int handle);
        [JSImport("raise", "interoper")] internal static partial void Raise(int handle, string eventName, string payloadJson);
    }
}
