using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Interoper.E2E.Client;

/// <summary>The thinnest [JSImport] path to interoper.js, until the runtime has its transports.</summary>
[SupportedOSPlatform("browser")]
internal static partial class JsImportGlue
{
    private const string Module = "interoper";

    public static Task ImportAsync() => JSHost.ImportAsync(Module, Smoke.InteroperUrl);

    [JSImport("start", Module)] public static partial Task Start(string metadataSpecifier);
    [JSImport("invoke", Module)] public static partial Task<string> Invoke(string opJson);
    [JSImport("invokeSync", Module)] public static partial string InvokeSync(string opJson);
    [JSImport("census", Module)] public static partial int Census();
}
