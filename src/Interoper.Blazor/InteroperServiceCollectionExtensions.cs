using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Interoper;

/// <summary>The transport behind async-mode bindings, from configuration: <c>"Interop": { "Transport": … }</c>.</summary>
public enum JsTransportKind
{
    /// <summary><see cref="JsImport"/> when .NET runs in the browser (WebAssembly), else <see cref="JsRuntime"/>.</summary>
    Auto,

    /// <summary><see cref="JsRuntimeTransport"/>: works on every host; on WebAssembly it takes the Server/Hybrid path.</summary>
    JsRuntime,

    /// <summary><see cref="JsImportTransport"/>: WebAssembly only; elsewhere the realm fails to start.</summary>
    JsImport,
}

public static class InteroperServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped <see cref="JsRealm"/> over the generated metadata module and live types (design §3.1).
    /// Call it in the server app and in the WebAssembly client, so prerendered components can be built too.
    /// </summary>
    public static IServiceCollection AddInteroper(this IServiceCollection services, string metadataSpecifier, JsClassRegistry classes) =>
        services.AddScoped(provider =>
        {
            // Resolved where the realm lives, so under InteractiveAuto `Auto` follows the component between hosts.
            var kind = Resolve(provider.GetService<IConfiguration>()?["Interop:Transport"]);
            var js = provider.GetRequiredService<IJSRuntime>();
            return new JsRealm(() => StartAsync(kind, js, metadataSpecifier), classes);
        });

    private static JsTransportKind Resolve(string? configured)
    {
        if (configured is null)
        {
            return OperatingSystem.IsBrowser() ? JsTransportKind.JsImport : JsTransportKind.JsRuntime;
        }
        if (!Enum.TryParse(configured, ignoreCase: true, out JsTransportKind kind) || !Enum.IsDefined(kind))
        {
            throw new InvalidOperationException($"Interop:Transport must be Auto, JsRuntime or JsImport, not '{configured}'.");
        }
        return kind == JsTransportKind.Auto ? Resolve(null) : kind;
    }

    private static async Task<IJsTransport> StartAsync(JsTransportKind kind, IJSRuntime js, string metadataSpecifier)
    {
        if (kind == JsTransportKind.JsRuntime)
        {
            return await JsRuntimeTransport.StartAsync(js, metadataSpecifier);
        }
#pragma warning disable CA1416 // outside the browser, StartAsync fails fast with the reason
        return await JsImportTransport.StartAsync(metadataSpecifier);
#pragma warning restore CA1416
    }
}
