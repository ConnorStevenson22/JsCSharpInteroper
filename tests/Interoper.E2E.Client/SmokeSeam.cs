namespace Interoper.E2E.Client;

public enum SmokeSeam
{
    /// <summary>[JSImport] → <c>invokeSync</c>: sync mode.</summary>
    JsImportSync,

    /// <summary>[JSImport] → <c>invoke</c>: async mode on WebAssembly.</summary>
    JsImportAsync,

    /// <summary>IJSRuntime → <c>invoke</c>: async mode on Server and Hybrid, or forced on WebAssembly.</summary>
    JsRuntime,
}
