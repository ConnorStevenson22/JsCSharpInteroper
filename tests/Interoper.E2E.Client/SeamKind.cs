namespace Interoper.E2E.Client;

public enum SeamKind
{
    /// <summary>JsImportTransport's sync seam (<c>invokeSync</c>): sync mode.</summary>
    JsImportSync,

    /// <summary>JsImportTransport's async seam: async mode on WebAssembly.</summary>
    JsImportAsync,

    /// <summary>JsRuntimeTransport: async mode on Server and Hybrid, or forced on WebAssembly.</summary>
    JsRuntime,

    /// <summary>A JsImportTransport and a JsRuntimeTransport side by side on one WebAssembly page.</summary>
    TwoTransports,
}
