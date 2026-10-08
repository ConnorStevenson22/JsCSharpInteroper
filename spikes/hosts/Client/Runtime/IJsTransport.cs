namespace HostsSpike.Client.Runtime;

/// <summary>
/// The only seam between generated code and a Blazor host. Generated live objects call this and
/// nothing else, so the same generated assembly runs on WebAssembly, Server and Hybrid.
/// Every member returns a ValueTask: transports that complete synchronously ([JSImport]) don't allocate.
/// </summary>
public interface IJsTransport
{
    string Name { get; }
    ValueTask<int> CreateAsync(string jsClass, string propertiesJson);
    ValueTask<double> GetNumberAsync(int handle, string property);
    ValueTask SetNumberAsync(int handle, string property, double value);
    ValueTask<string> SnapshotAsync(int handle, string properties);
    ValueTask SubscribeAsync(int handle, string eventName, int callbackId);
    ValueTask ReleaseAsync(int handle);

    /// <summary>Test hook: make the JS object raise an event, as user input would.</summary>
    ValueTask RaiseAsync(int handle, string eventName, string payloadJson);
}
