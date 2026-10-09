namespace Interoper.Runtime.Tests;

/// <summary>What the transport contract doesn't reach: envelope edge cases, the registry, the rarer in-memory ops.</summary>
public sealed class TransportTests
{
    [Fact]
    public void EnvelopeReadsAValueUndefinedAndAnError()
    {
        Assert.Equal(5, JsEnvelope.Read("""{"v":5}""", Json.ReadDouble));
        Assert.Equal(0, JsEnvelope.Read("{}", Json.ReadDouble)); // undefined is default
        Assert.Equal(default, JsEnvelope.Read("{}", (ref reader) => JsJson.ReadOpt(ref reader, Json.ReadDouble))); // R3

        // A batch error's per-op list is left for JsBatchException.
        var e = Assert.Throws<JsException>(() =>
            JsEnvelope.Check("""{"e":{"name":"BatchError","errors":[{"index":0,"name":"Error","message":"x"}],"message":"1 of 2 batched ops failed"}}"""));
        Assert.Equal(("BatchError", "1 of 2 batched ops failed", null), (e.Name, e.Message, e.JsStack));
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
            """{"v":{"center":{"$c":2,"type":"point","x":9,"y":8},"zoom":7,"basemap":"plain","selected":null}}""",
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
