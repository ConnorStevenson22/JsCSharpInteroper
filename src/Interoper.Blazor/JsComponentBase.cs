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

    /// <summary>
    /// Whether the component has been disposed, and its scope with it: code that awaits a JS call should stop when
    /// it is, since what it would use is released.
    /// </summary>
    protected bool IsDisposed { get; private set; }

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

    /// <summary>
    /// The live object of an element this component rendered and captured (R28), after its first render: its
    /// <paramref name="module"/>, when given, is imported first so the element is defined (sync mode preloads it
    /// instead); then it's wrapped through the async seam, in both modes. Null when the component is disposed
    /// meanwhile: the handle is released with the scope.
    /// </summary>
    protected async Task<T?> ElementAsync<T>(ElementReference element, string? module = null) where T : JsObject
    {
        try
        {
            if (module is not null && !IsDisposed)
            {
                await Scope.Realm.SendAsync(JsOp.Load([module]));
            }
            var result = IsDisposed ? null : await Scope.Realm.CallAsync(JsOp.Wrap(w => JsElement.Write(w, element)), Scope.Read<T>);
            return IsDisposed ? null : result;
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            return null; // the result reached the disposed scope, which released it
        }
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
    public virtual ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
