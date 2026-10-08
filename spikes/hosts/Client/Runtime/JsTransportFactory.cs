using Microsoft.JSInterop;

namespace HostsSpike.Client.Runtime;

public enum JsTransportMode
{
    /// <summary>[JSImport] when .NET runs inside the browser (WebAssembly), otherwise IJSRuntime (Server, Hybrid).</summary>
    Auto,

    /// <summary>Force [JSImport]. Only possible when .NET itself runs in the browser.</summary>
    JsImport,

    /// <summary>Force IJSRuntime. Works everywhere; on WebAssembly it reproduces Server/Hybrid behaviour.</summary>
    JsRuntime,
}

/// <summary>Bound from configuration ("Interop:Transport") in both the server and client apps.</summary>
public sealed class InteropOptions
{
    public JsTransportMode Transport { get; set; } = JsTransportMode.Auto;
}

public static class JsTransportFactory
{
    /// <summary>
    /// Resolved per component at runtime, not per app: under InteractiveAuto the same component can run
    /// on the server for one visit and in WebAssembly for the next.
    /// </summary>
    public static async Task<IJsTransport> CreateAsync(IJSRuntime js, JsTransportMode mode)
    {
        if (mode == JsTransportMode.Auto)
            mode = OperatingSystem.IsBrowser() ? JsTransportMode.JsImport : JsTransportMode.JsRuntime;

        if (mode == JsTransportMode.JsImport)
        {
            if (!OperatingSystem.IsBrowser())
                throw new PlatformNotSupportedException(
                    "Transport 'JsImport' needs .NET running inside the browser (Blazor WebAssembly). " +
                    "Server and Hybrid run .NET outside the browser; use 'Auto' or 'JsRuntime'.");
            return await JsImportTransport.CreateAsync();
        }

        return await JsRuntimeTransport.CreateAsync(js);
    }

    /// <summary>For code generated in sync mode. Only WebAssembly can run it.</summary>
    public static async Task<IJsSyncTransport> CreateSyncAsync()
    {
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException(
                "These bindings were generated in sync mode, which only runs on Blazor WebAssembly. " +
                "Server and Hybrid run .NET outside the browser; regenerate with --api async.");
        return await JsImportTransport.CreateAsync();
    }
}
