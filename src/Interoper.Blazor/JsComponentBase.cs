using Microsoft.AspNetCore.Components;

namespace Interoper;

/// <summary>
/// A component that uses live JS objects (design §3.2). It owns a <see cref="JsScope"/>, released when the
/// component is disposed; starts the realm before its first interactive render, so live calls work from
/// <c>OnInitializedAsync</c> on; and runs JS callbacks on its dispatcher.
/// </summary>
public abstract class JsComponentBase : ComponentBase, IAsyncDisposable
{
    private JsScope? scope;

    [Inject]
    private JsRealm Realm { get; set; } = null!;

    /// <summary>
    /// The handles and callbacks this component owns. While it prerenders, live calls through it throw
    /// <see cref="JsRealmUnavailableException"/>.
    /// </summary>
    protected JsScope Scope => scope ??= new JsScope(Realm) { Dispatch = RunCallbackAsync };

    public override Task SetParametersAsync(ParameterView parameters)
    {
        var started = RendererInfo.IsInteractive ? Realm.StartAsync() : Task.CompletedTask;
        return started.IsCompletedSuccessfully ? base.SetParametersAsync(parameters) : StartThenSetAsync(started, parameters);
    }

    private async Task StartThenSetAsync(Task started, ParameterView parameters)
    {
        parameters.SetParameterProperties(this); // a ParameterView is valid only until the first await
        await started;
        await base.SetParametersAsync(ParameterView.Empty);
    }

    // A handler that throws is reported like an event handler's exception: to the component and its error boundary.
    private async Task RunCallbackAsync(Action callback)
    {
        try
        {
            await InvokeAsync(callback);
        }
        catch (Exception e)
        {
            await DispatchExceptionAsync(e);
        }
    }

    /// <summary>Releases the scope. Override to dispose more, and call this too.</summary>
    public virtual ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
}
