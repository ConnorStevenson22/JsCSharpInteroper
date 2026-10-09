using Microsoft.JSInterop;

namespace Interoper.Runtime.Tests;

/// <summary>What the transport contract doesn't reach: envelope edge cases, the registry, the rarer in-memory ops.</summary>
public sealed class TransportTests
{
    [Fact]
    public void EnvelopeReadsAValueUndefinedAndAnError()
    {
        Assert.Equal(5, JsEnvelope.Read("""{"v":5}""", JsJson.ReadNumber));
        Assert.Equal(0, JsEnvelope.Read("{}", JsJson.ReadNumber)); // undefined is default
        Assert.Equal(default, JsEnvelope.Read("{}", (ref reader) => JsJson.ReadOpt(ref reader, JsJson.ReadNumber))); // R3

        // A batch error lists each failed op with its index.
        var e = Assert.Throws<JsBatchException>(() => JsEnvelope.Check(
            """{"e":{"name":"BatchError","errors":[{"index":0,"name":"Error","message":"x"},{"name":"TypeError","index":3,"message":"y","stack":"s"}],"message":"2 of 4 batched ops failed"}}"""));
        Assert.Equal(("BatchError", "2 of 4 batched ops failed", null), (e.Name, e.Message, e.JsStack));
        Assert.Equal(
            [(0, "Error", "x", null), (3, "TypeError", "y", "s")],
            e.Errors.Select(x => (x.Index, x.Error.Name, x.Error.Message, x.Error.JsStack)));
    }

    [Fact]
    public void ARegistryCallsHandlersUntilTheyAreReleased() // R21
    {
        var callbacks = new JsCallbacks();
        var id = callbacks.Register(args => args);
        Assert.Equal("[1]", callbacks.InvokeCallback(id, "[1]"));
        Assert.Equal(1, callbacks.Count);

        callbacks.Release(id);
        Assert.Null(callbacks.InvokeCallback(id, "[1]")); // undefined: an event already on its way is dropped
        Assert.Equal(0, callbacks.Count);
    }

    [Fact]
    public void InMemorySnapshotAndApply() // R17, R18
    {
        var transport = InMemoryDemoMaps.Create();
        Assert.Equal("""{"v":{"$h":1,"$t":5}}""", transport.Invoke("""{"op":"create","t":5,"args":[]}"""));
        JsEnvelope.Check(transport.Invoke("""{"op":"apply","h":1,"value":{"zoom":7,"center":{"$c":2,"type":"point","x":9,"y":8}}}"""));

        Assert.Equal(
            """{"v":{"center":{"$c":2,"type":"point","x":9,"y":8},"zoom":7,"rotation":0,"basemap":"plain"}}""",
            transport.Invoke("""{"op":"snapshot","h":1}"""));
    }

    [Fact]
    public void InMemoryReleaseReleasesEveryIdWhenADisposeThrows()
    {
        var transport = new InMemoryTransport().Define(new(1, "Faulty", _ => new Faulty()));
        JsEnvelope.Check(transport.Invoke("""{"op":"create","t":1}"""));
        var subscribe = """{"op":"subscribe","h":1,"method":"onFaulty","name":"x","callback":null}""";
        JsEnvelope.Check(transport.Invoke(subscribe));
        JsEnvelope.Check(transport.Invoke(subscribe));

        var e = Assert.Throws<JsException>(() => JsEnvelope.Check(transport.Invoke("""{"op":"release","ids":[2,1,3]}""")));
        Assert.Equal(("AggregateError", "2 dispose() calls failed"), (e.Name, e.Message));
        Assert.Equal(0, transport.Census());
    }

    [Fact]
    public void InMemoryRejectsForeignValues()
    {
        var transport = new InMemoryTransport().Define(new(1, "Odd", _ => new Odd()));
        JsEnvelope.Check(transport.Invoke("""{"op":"create","t":1}"""));
        var e = Assert.Throws<JsException>(() => JsEnvelope.Check(transport.Invoke("""{"op":"get","h":1,"name":"when"}""")));
        Assert.Equal(("Error", $"A {typeof(DateTime)} is not an in-memory JS value; see InMemoryObject."), (e.Name, e.Message));
    }

    [Fact]
    public async Task JsRuntimeTransportMapsJSExceptionAndPassesDisconnectsThrough()
    {
        var js = new StubRuntime { FailOn = "invoke", Error = new JSException("boom") };
        var transport = await JsRuntimeTransport.StartAsync(js, "/meta.js");
        var e = await Assert.ThrowsAsync<JsException>(() => transport.InvokeAsync("{}"));
        Assert.Equal(("Error", "boom"), (e.Name, e.Message));

        js = new StubRuntime { FailOn = "startWithDotNetObject", Error = new JSDisconnectedException("gone") };
        await Assert.ThrowsAsync<JSDisconnectedException>(() => JsRuntimeTransport.StartAsync(js, "/meta.js"));
        Assert.True(js.Disposed); // a failed start disposes the module
    }

    private sealed class Odd : InMemoryObject
    {
        public override string ClassName => "Odd";

        public override object? Get(string name) => name == "when" ? DateTime.UnixEpoch : base.Get(name);
    }

    private sealed class Faulty : InMemoryObject
    {
        public override string ClassName => "Faulty";

        public override object? Call(string name, object?[] args) => name == "onFaulty" ? new Throwing() : base.Call(name, args);
    }

    private sealed class Throwing : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }
}
