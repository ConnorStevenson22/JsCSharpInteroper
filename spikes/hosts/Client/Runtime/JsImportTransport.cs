using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace HostsSpike.Client.Runtime;

/// <summary>
/// WebAssembly: synchronous [JSImport] calls. Serves sync-mode code directly, and async-mode code
/// through already-completed Tasks.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class JsImportTransport : IJsTransport, IJsSyncTransport
{
    private JsImportTransport() { }

    public string Name => "WebAssembly ([JSImport])";

    public static async Task<JsImportTransport> CreateAsync()
    {
        await JSHost.ImportAsync("interoper", "/interoper.js");
        await Native.UseWasmExports(typeof(JsImportTransport).Assembly.GetName().Name + ".dll");
        return new JsImportTransport();
    }

    // sync mode
    public int Create(string jsClass, string propertiesJson) => Native.Create(jsClass, propertiesJson);
    public double GetNumber(int handle, string property) => Native.GetNumber(handle, property);
    public void SetNumber(int handle, string property, double value) => Native.SetNumber(handle, property, value);
    public string Snapshot(int handle, string properties) => Native.Snapshot(handle, properties);
    public void Subscribe(int handle, string eventName, int callbackId) => Native.Subscribe(handle, eventName, callbackId);
    public void Release(int handle) => Native.Release(handle);
    public void Raise(int handle, string eventName, string payloadJson) => Native.Raise(handle, eventName, payloadJson);

    // async mode
    public Task<int> CreateAsync(string jsClass, string propertiesJson) => Task.FromResult(Create(jsClass, propertiesJson));
    public Task<double> GetNumberAsync(int handle, string property) => Task.FromResult(GetNumber(handle, property));
    public Task<string> SnapshotAsync(int handle, string properties) => Task.FromResult(Snapshot(handle, properties));

    public Task SetNumberAsync(int handle, string property, double value)
    {
        SetNumber(handle, property, value);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(int handle, string eventName, int callbackId)
    {
        Subscribe(handle, eventName, callbackId);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(int handle)
    {
        Release(handle);
        return Task.CompletedTask;
    }

    public Task RaiseAsync(int handle, string eventName, string payloadJson)
    {
        Raise(handle, eventName, payloadJson);
        return Task.CompletedTask;
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
