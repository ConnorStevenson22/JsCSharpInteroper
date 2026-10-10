using Microsoft.JSInterop;

namespace Interoper.Runtime.Tests;

/// <summary>Serves as the runtime and as the imported module; fails one identifier.</summary>
internal sealed class StubRuntime : IJSRuntime, IJSObjectReference
{
    public string FailOn { get; init; } = "";
    public Exception Error { get; init; } = null!;
    public bool Disposed { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        identifier == FailOn ? ValueTask.FromException<TValue>(Error)
        : identifier == "import" ? new((TValue)(object)this)
        : new(default(TValue)!);

#pragma warning disable BL0016 // a stub forwarding to itself, not a component's JS call
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);
#pragma warning restore BL0016

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return default;
    }
}

/// <summary>An <see cref="InMemoryTransport"/> that records every op sent through either seam, and can fail them.</summary>
internal sealed class RecordingTransport(InMemoryTransport inner) : IJsSyncTransport, IJsTransport
{
    public List<string> Ops { get; } = [];

    /// <summary>Thrown by every op while set, as a dropped circuit would.</summary>
    public Exception? Fail { get; set; }

    public JsCallbacks Callbacks => inner.Callbacks;

    public string Invoke(string opJson) => Record(opJson) ? inner.Invoke(opJson) : throw Fail!;

    /// <summary>Async ops sent while it's set wait for this task, then run in order, as ops in flight on a slow connection would.</summary>
    public Task? Hold { get; set; }

    private Task held = Task.CompletedTask;

    public Task<string> InvokeAsync(string opJson) =>
        !Record(opJson) ? Task.FromException<string>(Fail!) : Hold is { } hold ? Held(hold, opJson) : inner.InvokeAsync(opJson);

    private Task<string> Held(Task hold, string opJson)
    {
        var previous = held;
        var run = Run();
        held = run;
        return run;

        async Task<string> Run()
        {
            await hold;
            await Task.WhenAny(previous); // in order, whether it failed or not
            return await inner.InvokeAsync(opJson);
        }
    }

    private bool Record(string opJson)
    {
        Ops.Add(opJson);
        return Fail is null;
    }

    public int Census() => inner.Census();
    public Task<int> CensusAsync() => inner.CensusAsync();
    public double GetNumber(int handle, string name) => inner.GetNumber(handle, name);
    public string GetString(int handle, string name) => inner.GetString(handle, name);
    public bool GetBoolean(int handle, string name) => inner.GetBoolean(handle, name);
    public void SetNumber(int handle, string name, double value) => inner.SetNumber(handle, name, value);
    public void SetString(int handle, string name, string value) => inner.SetString(handle, name, value);
    public void SetBoolean(int handle, string name, bool value) => inner.SetBoolean(handle, name, value);
    public Task<double> GetNumberAsync(int handle, string name) => inner.GetNumberAsync(handle, name);
    public Task<string> GetStringAsync(int handle, string name) => inner.GetStringAsync(handle, name);
    public Task<bool> GetBooleanAsync(int handle, string name) => inner.GetBooleanAsync(handle, name);
    public Task SetNumberAsync(int handle, string name, double value) => inner.SetNumberAsync(handle, name, value);
    public Task SetStringAsync(int handle, string name, string value) => inner.SetStringAsync(handle, name, value);
    public Task SetBooleanAsync(int handle, string name, bool value) => inner.SetBooleanAsync(handle, name, value);
}
