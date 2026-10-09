using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Interoper;

/// <summary>
/// WebAssembly: <c>[JSImport]</c> calls into interoper.js on JS's own thread. Serves both seams; the async
/// seam's typed fast paths return completed Tasks, and its ops go through <c>invoke</c>.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class JsImportTransport : IJsSyncTransport, IJsTransport
{
    private const string Module = "interoper";

    private JsImportTransport() { }

    public JsCallbacks Callbacks { get; } = new();

    /// <summary>
    /// Imports interoper.js and starts it with the generated metadata module. One transport per page: JS has
    /// one callback sink.
    /// </summary>
    public static async Task<JsImportTransport> StartAsync(string metadataSpecifier)
    {
        // Sync bindings reach JS only through here, so this is where they fail fast on Server and Hybrid.
        if (!OperatingSystem.IsBrowser())
        {
            throw new InvalidOperationException(
                "These bindings were generated in sync mode, which only runs on Blazor WebAssembly. This is .NET " +
                "outside the browser (Server or Hybrid), where [JSImport] can't reach JS: regenerate with `--api async`.");
        }
        await JSHost.ImportAsync(Module, JsRuntimeTransport.ModuleUrl);
        var transport = new JsImportTransport();
        await Native.Start(metadataSpecifier, transport.Callbacks.InvokeCallback);
        return transport;
    }

    public string Invoke(string opJson) => Native.InvokeSync(opJson);

    public int Census() => Native.Census();

    // The fast paths throw a bare JSException; interoper.js keeps the JS error for takeError.
    public double GetNumber(int handle, string name)
    {
        try { return Native.GetNumber(handle, name); }
        catch (JSException e) { throw Mapped(e); }
    }

    public string? GetString(int handle, string name)
    {
        try { return Native.GetString(handle, name); }
        catch (JSException e) { throw Mapped(e); }
    }

    public bool GetBoolean(int handle, string name)
    {
        try { return Native.GetBoolean(handle, name); }
        catch (JSException e) { throw Mapped(e); }
    }

    public void SetNumber(int handle, string name, double value)
    {
        try { Native.SetNumber(handle, name, value); }
        catch (JSException e) { throw Mapped(e); }
    }

    public void SetString(int handle, string name, string? value)
    {
        try { Native.SetString(handle, name, value); }
        catch (JSException e) { throw Mapped(e); }
    }

    public void SetBoolean(int handle, string name, bool value)
    {
        try { Native.SetBoolean(handle, name, value); }
        catch (JSException e) { throw Mapped(e); }
    }

    public Task<string> InvokeAsync(string opJson) => Native.Invoke(opJson);

    public Task<int> CensusAsync() => Task.FromResult(Census());

    // Async callers get errors in the Task, as from any other transport. No closures: these are hot paths.
    public Task<double> GetNumberAsync(int handle, string name)
    {
        try { return Task.FromResult(GetNumber(handle, name)); }
        catch (Exception e) { return Task.FromException<double>(e); }
    }

    public Task<string?> GetStringAsync(int handle, string name)
    {
        try { return Task.FromResult(GetString(handle, name)); }
        catch (Exception e) { return Task.FromException<string?>(e); }
    }

    public Task<bool> GetBooleanAsync(int handle, string name)
    {
        try { return Task.FromResult(GetBoolean(handle, name)); }
        catch (Exception e) { return Task.FromException<bool>(e); }
    }

    public Task SetNumberAsync(int handle, string name, double value)
    {
        try { SetNumber(handle, name, value); return Task.CompletedTask; }
        catch (Exception e) { return Task.FromException(e); }
    }

    public Task SetStringAsync(int handle, string name, string? value)
    {
        try { SetString(handle, name, value); return Task.CompletedTask; }
        catch (Exception e) { return Task.FromException(e); }
    }

    public Task SetBooleanAsync(int handle, string name, bool value)
    {
        try { SetBoolean(handle, name, value); return Task.CompletedTask; }
        catch (Exception e) { return Task.FromException(e); }
    }

    private static Exception Mapped(JSException e) => Native.TakeError() is { } envelope ? JsEnvelope.Error(envelope) : e;

    private static partial class Native
    {
        [JSImport("start", Module)]
        public static partial Task Start(
            string metadataSpecifier,
            [JSMarshalAs<JSType.Function<JSType.Number, JSType.String, JSType.String>>] Func<int, string, string?> sink);

        [JSImport("invokeSync", Module)] public static partial string InvokeSync(string opJson);
        [JSImport("invoke", Module)] public static partial Task<string> Invoke(string opJson);
        [JSImport("census", Module)] public static partial int Census();
        [JSImport("takeError", Module)] public static partial string? TakeError();
        [JSImport("getValue", Module)] public static partial double GetNumber(int handle, string name);
        [JSImport("getValue", Module)] public static partial string? GetString(int handle, string name);
        [JSImport("getValue", Module)] public static partial bool GetBoolean(int handle, string name);
        [JSImport("setValue", Module)] public static partial void SetNumber(int handle, string name, double value);
        [JSImport("setValue", Module)] public static partial void SetString(int handle, string name, string? value);
        [JSImport("setValue", Module)] public static partial void SetBoolean(int handle, string name, bool value);
    }
}
