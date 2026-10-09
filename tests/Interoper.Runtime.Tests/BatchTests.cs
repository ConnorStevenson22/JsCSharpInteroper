using System.Text.Json.Nodes;

namespace Interoper.Runtime.Tests;

/// <summary>Batching (design §3.3) over the in-memory fixture fakes; the contract suite covers it on every host.</summary>
public sealed class BatchTests
{
    private readonly RecordingTransport transport = new(InMemoryDemoMaps.Create());
    private readonly JsRealm realm;
    private readonly JsScope scope;

    public BatchTests()
    {
        realm = new JsRealm(() => Task.FromResult<IJsTransport>(transport), new JsClassRegistry());
        realm.StartAsync().GetAwaiter().GetResult();
        scope = new JsScope(realm);
    }

    [Fact]
    public async Task QueuedTasksCompleteAtOnceAndTheBatchIsOneOp()
    {
        var h = await CreateView();
        var start = realm.RoundTrips;
        await using (scope.BeginBatch())
        {
            Assert.True(realm.SetAsync(h, "zoom", 2).IsCompletedSuccessfully);
            Assert.True(realm.SendAsync(Set(h, "rotation", 15)).IsCompletedSuccessfully);
            Assert.Empty(transport.Ops);
        }
        var batch = JsonNode.Parse(Assert.Single(transport.Ops))!;
        Assert.Equal("batch", (string?)batch["op"]);
        Assert.Equal(["zoom", "rotation"], batch["ops"]!.AsArray().Select(op => (string?)op!["name"]));
        Assert.Equal(start + 1, realm.RoundTrips);
    }

    [Fact]
    public async Task ANestedBatchJoinsTheOuterOne()
    {
        var h = await CreateView();
        await using (scope.BeginBatch())
        {
            await using (scope.BeginBatch())
            {
                await realm.SetAsync(h, "zoom", 2);
            }
            Assert.Empty(transport.Ops); // the inner one sent nothing
            await realm.SetAsync(h, "zoom", 3);
        }
        Assert.Single(transport.Ops);
        Assert.Equal(3, await realm.GetNumberAsync(h, "zoom"));
    }

    [Fact]
    public async Task OnlyTheFlowInsideTheBatchIsQueued()
    {
        var h = await CreateView();
        var inside = new TaskCompletionSource();
        var other = Other(); // started outside the batch, like another component's handler
        await using (scope.BeginBatch())
        {
            await realm.SetAsync(h, "zoom", 2);
            inside.SetResult();
            await other;
            Assert.Equal("set", (string?)JsonNode.Parse(Assert.Single(transport.Ops))!["op"]); // sent at once
        }
        Assert.Equal((2, 30), (await realm.GetNumberAsync(h, "zoom"), await realm.GetNumberAsync(h, "rotation")));

        async Task Other()
        {
            await inside.Task;
            await realm.SendAsync(Set(h, "rotation", 30));
        }
    }

    [Fact]
    public async Task OutsideABatchErrorsSurfaceAtTheCall()
    {
        var e = await Assert.ThrowsAsync<JsException>(() => realm.SendAsync("""{"op":"set","h":999,"name":"zoom","value":1}"""));
        Assert.Equal(("InteroperError", "Unknown handle: 999"), (e.Name, e.Message));
    }

    [Fact]
    public async Task AnUnknownHandleFailsTheWholeBatch()
    {
        var h = await CreateView();
        var e = await Assert.ThrowsAsync<JsException>(async () =>
        {
            await using (scope.BeginBatch())
            {
                await realm.SetAsync(h, "zoom", 5);
                await realm.SendAsync($$$"""{"op":"set","h":{{{h}}},"name":"selected","value":{"$h":999}}""");
            }
        });
        Assert.IsNotType<JsBatchException>(e); // it fails while parsing, before any op runs
        Assert.Equal(1, await realm.GetNumberAsync(h, "zoom"));
    }

    [Fact]
    public async Task DisposingTheScopeSendsTheQueueBeforeTheRelease()
    {
        var h = await CreateView();
        var batch = scope.BeginBatch();
        await realm.SetAsync(h, "zoom", 2);
        await scope.DisposeAsync();
        Assert.Equal(["batch", "release"], transport.Ops.Select(op => (string?)JsonNode.Parse(op)!["op"]));
        Assert.Equal(0, transport.Census());

        await batch.DisposeAsync(); // nothing left to send
        Assert.Equal(2, transport.Ops.Count);
        Assert.Throws<ObjectDisposedException>(scope.BeginBatch);
    }

    [Fact]
    public async Task ADisposedScopeStillReleasesWhenItsQueueFails()
    {
        var batch = scope.BeginBatch();
        var h = await CreateView();
        await realm.SendAsync($$"""{"op":"call","h":{{h}},"name":"noSuchMethod"}""");
        await Assert.ThrowsAsync<JsBatchException>(() => scope.DisposeAsync().AsTask());
        Assert.Equal(0, transport.Census());
        await batch.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AScopeDisposedInsideAnotherScopesBatchReleasesAfterTheQueue(bool joined)
    {
        var other = new JsScope(realm);
        await using (scope.BeginBatch())
        {
            await using (joined ? other.BeginBatch() : null)
            {
                var view = await realm.CallAsync("""{"op":"create","t":5,"args":[]}""", static (ref reader) => JsonNode.Parse(ref reader));
                var h = (int)view!["$h"]!;
                other.Own(h);
                transport.Ops.Clear();
                await realm.SetAsync(h, "zoom", 2);
                await other.DisposeAsync();
            }
        }
        Assert.Equal(["batch", "release"], transport.Ops.Select(op => (string?)JsonNode.Parse(op)!["op"]));
        Assert.Equal(0, transport.Census());
    }

    [Fact]
    public async Task EachOpenBatchIsFlushedAndTheFirstErrorThrown()
    {
        var h = await CreateView();
        var second = await BeginElsewhere(); // first, or it would join this flow's batch
        var first = scope.BeginBatch();
        await realm.SendAsync($$"""{"op":"call","h":{{h}},"name":"first"}""");
        await second.Queue($$"""{"op":"call","h":{{h}},"name":"second"}""");
        var e = await Assert.ThrowsAsync<JsBatchException>(() => scope.DisposeAsync().AsTask());
        Assert.Contains("second", e.Errors.Single().Error.Message, StringComparison.Ordinal); // begun first, flushed first
        Assert.Equal(["batch", "batch", "release"], transport.Ops.Select(op => (string?)JsonNode.Parse(op)!["op"]));
        await first.DisposeAsync();
        await second.Batch.DisposeAsync();
    }

    // A batch begun on another async flow, with a way to queue on that flow.
    private async Task<(IAsyncDisposable Batch, Func<string, Task> Queue)> BeginElsewhere()
    {
        var queue = new TaskCompletionSource<(string Op, TaskCompletionSource Done)>();
        var begun = new TaskCompletionSource<IAsyncDisposable>();
        _ = Flow();
        return (await begun.Task, async op =>
        {
            var done = new TaskCompletionSource();
            queue.SetResult((op, done));
            await done.Task;
        }
        );

        async Task Flow()
        {
            await Task.Yield();
            begun.SetResult(scope.BeginBatch());
            var (op, done) = await queue.Task;
            await realm.SendAsync(op);
            done.SetResult();
        }
    }

    [Fact]
    public async Task SyncDisposalOfAScopeWithAnOpenBatchThrows()
    {
        await CreateView();
        var batch = scope.BeginBatch();
        var e = Assert.Throws<InvalidOperationException>(scope.Dispose);
        Assert.Equal("A scope with an open batch must be disposed with DisposeAsync.", e.Message);
        await batch.DisposeAsync();
        scope.Dispose(); // closed now
        Assert.Equal(0, transport.Census());
    }

    [Fact]
    public void BeginBatchNeedsAStartedRealm()
    {
        var unstarted = new JsScope(new JsRealm(() => throw new InvalidOperationException("never started"), new JsClassRegistry()));
        Assert.Throws<JsRealmUnavailableException>(unstarted.BeginBatch);
    }

    [Fact]
    public void InSyncModeAPromiseInABatchIsThatOpsFailure()
    {
        var memory = InMemoryDemoMaps.Create();
        var h = (int)JsonNode.Parse(memory.Invoke("""{"op":"create","t":5,"args":[]}"""))!["v"]!["$h"]!;
        var e = Assert.Throws<JsBatchException>(() => JsEnvelope.Check(memory.Invoke(
            $$"""{"op":"batch","ops":[{"op":"call","h":{{h}},"name":"flyTo","args":[[1,2]]},{{Set(h, "zoom", 9)}}]}""")));
        Assert.Equal((0, "The op returned a Promise; send it through invoke."), (e.Errors.Single().Index, e.Errors.Single().Error.Message));
        Assert.Equal(9, memory.GetNumber(h, "zoom"));
    }

    private async Task<int> CreateView()
    {
        var view = await realm.CallAsync("""{"op":"create","t":5,"args":[]}""", static (ref reader) => JsonNode.Parse(ref reader));
        scope.Own((int)view!["$h"]!);
        transport.Ops.Clear();
        return (int)view["$h"]!;
    }

    private static string Set(int h, string name, double value) => $$"""{"op":"set","h":{{h}},"name":"{{name}}","value":{{value}}}""";
}
