using Microsoft.JSInterop;

// BL0016 wants every JS interop call in a try/catch; the smoke page catches and prints errors in one place.
#pragma warning disable BL0016

namespace Interoper.E2E.Client;

/// <summary>The thinnest IJSRuntime path to interoper.js, until the runtime has its transports.</summary>
internal sealed class JsRuntimeGlue(IJSObjectReference module)
{
    public static async Task<JsRuntimeGlue> ImportAsync(IJSRuntime js) =>
        new(await js.InvokeAsync<IJSObjectReference>("import", Smoke.InteroperUrl));

    public Task Start(string metadataSpecifier) => module.InvokeVoidAsync("start", metadataSpecifier).AsTask();
    public Task<string> Invoke(string opJson) => module.InvokeAsync<string>("invoke", opJson).AsTask();
    public Task<int> Census() => module.InvokeAsync<int>("census").AsTask();
}
