namespace Interoper.Runtime.Tests;

/// <summary>The ops generated code writes (design §3.4), as interoper.js reads them.</summary>
public sealed class JsOpTests
{
    [Fact]
    public void OpsAreWrittenInTheWireFormat()
    {
        Assert.Equal("""{"op":"create","t":5,"args":[{"zoom":2}]}""", JsOp.Create(5, w => w.WriteNumber("zoom"u8, 2)));
        Assert.Equal("""{"op":"get","h":1,"name":"center","byValue":true}""", JsOp.Get(1, "center", byValue: true));
        Assert.Equal("""{"op":"set","h":1,"name":"zoom","value":3}""", JsOp.Set(1, "zoom", w => w.WriteNumberValue(3)));
        Assert.Equal("""{"op":"call","h":1,"name":"clone","args":[]}""", JsOp.Call(1, "clone"));
        Assert.Equal("""{"op":"call","module":"m.js","name":"f","args":[1,"a"],"byValue":true}""", JsOp.Call("m.js", "f", w =>
        {
            w.WriteNumberValue(1);
            w.WriteStringValue("a");
        }, byValue: true));
        Assert.Equal("""{"op":"snapshot","h":1}""", JsOp.Snapshot(1));
        Assert.Equal("""{"op":"apply","h":1,"value":{"zoom":3}}""", JsOp.Apply(1, w => w.WriteNumber("zoom"u8, 3)));
        Assert.Equal("""{"op":"subscribe","h":1,"method":"on","name":"click","callback":{"$f":2}}""", JsOp.Subscribe(1, "on", "click", 2));
        Assert.Equal("""{"op":"wrap","value":{"$e":"a1"}}""", JsOp.Wrap(w => JsElement.Write(w, new("a1")))); // R13, R28
    }

    [Fact] // R24
    public void SubscribeSendsEventOptionsOnlyWhenTheySetSomething()
    {
        const string Plain = """{"op":"subscribe","h":1,"method":"on","name":"click","callback":{"$f":2}}""";
        Assert.Equal(Plain, JsOp.Subscribe(1, "on", "click", 2, new EventOptions()));
        Assert.Equal(
            """{"op":"subscribe","h":1,"method":"on","name":"click","callback":{"$f":2},"options":{"preventDefault":true,"stopPropagation":true,"fields":["x","y"]}}""",
            JsOp.Subscribe(1, "on", "click", 2, new EventOptions { PreventDefault = true, StopPropagation = true, Fields = ["x", "y"] }));
        Assert.Equal(
            """{"op":"subscribe","h":1,"method":"on","name":"click","callback":{"$f":2},"options":{"fields":[]}}""",
            JsOp.Subscribe(1, "on", "click", 2, new EventOptions { Fields = [] }));
    }

    [Fact]
    public void ALiveArgumentIsItsHandle()
    {
        Assert.Equal("""{"$h":7}""", Json.Write<JsObject>(new Live(7), JsObject.Write));
    }

    private sealed class Live(int handle) : JsObject(null!, handle);
}
