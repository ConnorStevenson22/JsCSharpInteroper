namespace Interoper;

/// <summary>
/// The seam behind bindings generated with <c>--api sync</c> (design §3.1). Only WebAssembly provides it
/// (<see cref="JsImportTransport"/>), because only there does .NET share a thread with JS.
/// </summary>
public interface IJsSyncTransport
{
    /// <summary>The registry behind every <c>{"$f":id}</c> sent through this transport.</summary>
    JsCallbacks Callbacks { get; }

    /// <summary>
    /// Runs one op (design §3.4) and returns its envelope: <c>{"v":…}</c>, <c>{}</c> for undefined, or
    /// <c>{"e":…}</c>. Read it with <see cref="JsEnvelope"/>.
    /// </summary>
    string Invoke(string opJson);

    // Typed fast paths for primitive properties; errors throw JsException.
    double GetNumber(int handle, string name);
    string? GetString(int handle, string name);
    bool GetBoolean(int handle, string name);
    void SetNumber(int handle, string name, double value);
    void SetString(int handle, string name, string? value);
    void SetBoolean(int handle, string name, bool value);

    /// <summary>The number of live JS handles, for leak checks.</summary>
    int Census();
}

/// <summary>
/// The seam behind bindings generated with <c>--api async</c> (design §3.1). Every host provides it:
/// <see cref="JsImportTransport"/> on WebAssembly, <see cref="JsRuntimeTransport"/> anywhere.
/// </summary>
public interface IJsTransport
{
    /// <inheritdoc cref="IJsSyncTransport.Callbacks"/>
    JsCallbacks Callbacks { get; }

    /// <inheritdoc cref="IJsSyncTransport.Invoke"/>
    /// <remarks>A Promise result is awaited.</remarks>
    Task<string> InvokeAsync(string opJson);

    Task<double> GetNumberAsync(int handle, string name);
    Task<string?> GetStringAsync(int handle, string name);
    Task<bool> GetBooleanAsync(int handle, string name);
    Task SetNumberAsync(int handle, string name, double value);
    Task SetStringAsync(int handle, string name, string? value);
    Task SetBooleanAsync(int handle, string name, bool value);

    /// <inheritdoc cref="IJsSyncTransport.Census"/>
    Task<int> CensusAsync();
}
