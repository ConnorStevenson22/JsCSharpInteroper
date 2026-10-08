namespace HostsSpike.Client.Runtime;

/// <summary>
/// The seam behind code generated in <b>async</b> mode (<c>Task&lt;X&gt; GetXAsync()</c>). Every host can
/// provide it: [JSImport] on WebAssembly, IJSRuntime on Server and Hybrid.
/// </summary>
public interface IJsTransport
{
    string Name { get; }
    Task<int> CreateAsync(string jsClass, string propertiesJson);
    Task<double> GetNumberAsync(int handle, string property);
    Task SetNumberAsync(int handle, string property, double value);
    Task<string> SnapshotAsync(int handle, string properties);
    Task SubscribeAsync(int handle, string eventName, int callbackId);
    Task ReleaseAsync(int handle);

    /// <summary>Test hook: make the JS object raise an event, as user input would.</summary>
    Task RaiseAsync(int handle, string eventName, string payloadJson);
}

/// <summary>
/// The seam behind code generated in <b>sync</b> mode (<c>X Zoom { get; set; }</c>). Only WebAssembly can
/// provide it, because only there does .NET share a thread with JS.
/// </summary>
public interface IJsSyncTransport
{
    string Name { get; }
    int Create(string jsClass, string propertiesJson);
    double GetNumber(int handle, string property);
    void SetNumber(int handle, string property, double value);
    string Snapshot(int handle, string properties);
    void Subscribe(int handle, string eventName, int callbackId);
    void Release(int handle);
    void Raise(int handle, string eventName, string payloadJson);
}
