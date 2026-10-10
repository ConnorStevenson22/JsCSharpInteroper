using Interoper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Interoper.Runtime.Tests;

/// <summary>
/// IOP3001 (mapping §8) on snippets compiled against the real runtime and the golden slice's async bindings: each one
/// is the body of an async method with a scope, a view, a marker, a non-live <c>other</c> and a <c>level</c>.
/// </summary>
public sealed class ChattyAwaitAnalyzerTests
{
    [Fact] // IOP3001: once per run, on its second statement
    public async Task ItFlagsConsecutiveAwaitedWrites()
    {
        var found = await Analyze("""
            await view.SetZoomAsync(5);
            await view.SetRotationAsync(15);
            await marker.SetLabelAsync("a");
            var zoom = await view.GetZoomAsync();
            await view.FlyToAsync(new FlyToTarget(new double[] { 1, 2 }));
            await view.ApplyAsync(new MapViewSnapshot { Zoom = zoom });
            """);
        Assert.Equal(["await view.SetRotationAsync(15);", "await view.ApplyAsync(new MapViewSnapshot { Zoom = zoom });"], found.Select(d => d.Code));
        Assert.All(found, d => Assert.Equal(("IOP3001", DiagnosticSeverity.Info), (d.Id, d.Severity)));
    }

    [Fact] // IOP3001, design §3.3
    public async Task ItDoesNotFlagWritesInABatch()
    {
        var found = await Analyze("""
            await using (scope.BeginBatch())
            {
                await view.SetZoomAsync(5);
                if (level > 1)
                {
                    await view.SetRotationAsync(15);
                    await marker.SetLabelAsync("a");
                }
            }
            {
                await using var batch = scope.BeginBatch();
                await view.SetZoomAsync(5);
                await view.SetRotationAsync(15);
            }
            await view.SetZoomAsync(5);
            await view.SetRotationAsync(15); // after the batch
            """);
        Assert.Equal(["await view.SetRotationAsync(15); // after the batch"], found.Select(d => d.Code));
    }

    [Fact] // IOP3001: a lambda in a batch may run after it
    public async Task ALambdaIsNotInItsMethodsBatch()
    {
        var found = await Analyze("""
            await using (scope.BeginBatch())
            {
                Func<Task> later = async () =>
                {
                    await view.SetZoomAsync(5);
                    await view.SetRotationAsync(15);
                };
            }
            """);
        Assert.Equal(["await view.SetRotationAsync(15);"], found.Select(d => d.Code));
    }

    [Fact] // IOP3001: a read returns Task<T>, and flushes a batch anyway
    public async Task ItDoesNotFlagReads()
    {
        Assert.Empty(await Analyze("""
            await view.GetZoomAsync();
            await view.GetRotationAsync();
            await view.SetZoomAsync(5);
            await view.GetZoomAsync();
            await view.SetRotationAsync(15);
            await view.SnapshotAsync();
            """));
    }

    [Fact] // IOP3001: only instance methods of live objects
    public async Task ItDoesNotFlagOtherReceivers()
    {
        Assert.Empty(await Analyze("""
            await Task.Delay(1);
            await Task.Delay(1);
            await other.WriteAsync();
            await other.WriteAsync();
            await Measure.PerimeterAsync(scope.Realm);
            await Measure.PerimeterAsync(scope.Realm);
            await scope.DisposeAsync();
            await scope.DisposeAsync();
            """));
    }

    [Fact] // IOP3001: also with ConfigureAwait
    public async Task ItFlagsWritesWithConfigureAwait()
    {
        var found = await Analyze("""
            await view.SetZoomAsync(5).ConfigureAwait(false);
            await view.SetRotationAsync(15).ConfigureAwait(false);
            """);
        Assert.Equal(["await view.SetRotationAsync(15).ConfigureAwait(false);"], found.Select(d => d.Code));
    }

    [Fact] // IOP3001: a run is consecutive statements, in a block or a switch section
    public async Task ARunIsConsecutiveStatements()
    {
        var found = await Analyze("""
            await view.SetZoomAsync(5);
            level++;
            await view.SetRotationAsync(15);
            switch (level)
            {
                case 1:
                    await view.SetZoomAsync(5);
                    await view.SetRotationAsync(15); // in a case
                    break;
            }
            """);
        Assert.Equal(["await view.SetRotationAsync(15); // in a case"], found.Select(d => d.Code));
    }

    [Fact] // IOP3001, design §3.3: however the batch is declared
    public async Task ItDoesNotFlagWritesInABatchFromALocal()
    {
        Assert.Empty(await Analyze("""
            var batch = scope.BeginBatch();
            await using (batch)
            {
                await view.SetZoomAsync(5);
                await view.SetRotationAsync(15);
            }
            {
                await using IAsyncDisposable typed = scope.BeginBatch();
                await view.SetZoomAsync(5);
                await view.SetRotationAsync(15);
            }
            switch (level)
            {
                case 1:
                    await using (var b = scope.BeginBatch())
                    {
                        await view.SetZoomAsync(5);
                        await view.SetRotationAsync(15);
                    }
                    break;
            }
            """));
    }

    [Fact] // IOP3001: a local function in a batch may run after it
    public async Task ALocalFunctionIsNotInItsMethodsBatch()
    {
        var found = await Analyze("""
            await using (scope.BeginBatch())
            {
                async Task Later()
                {
                    await view.SetZoomAsync(5);
                    await view.SetRotationAsync(15);
                }
                await Later();
            }
            """);
        Assert.Equal(["await view.SetRotationAsync(15);"], found.Select(d => d.Code));
    }

    [Fact] // IOP3001: top-level statements too
    public async Task ItAnalyzesTopLevelStatements()
    {
        Assert.Equal(["await view.SetRotationAsync(15);"], (await AnalyzeTopLevel("""
            await view.SetZoomAsync(5);
            await view.SetRotationAsync(15);
            """)).Select(d => d.Code));
        Assert.Empty(await AnalyzeTopLevel("""
            await using var batch = scope.BeginBatch();
            await view.SetZoomAsync(5);
            await view.SetRotationAsync(15);
            """));
    }

    private sealed record Found(string Id, DiagnosticSeverity Severity, string Code);

    private static Task<List<Found>> Analyze(string body) => Compile($$"""
        public static class Snippet
        {
            public static async Task Run(JsScope scope, MapView view, Marker marker, Other other, double level)
            {
        {{body}}
            }
        }
        """, OutputKind.DynamicallyLinkedLibrary);

    // Top-level statements, with the same locals.
    private static Task<List<Found>> AnalyzeTopLevel(string body) => Compile($$"""
        JsScope scope = null!;
        MapView view = null!;
        {{body}}
        """, OutputKind.ConsoleApplication);

    private static async Task<List<Found>> Compile(string code, OutputKind kind)
    {
        var source = $$"""
            using System;
            using System.Threading.Tasks;
            using GoldenSlice.Async;
            using Interoper;

            {{code}}

            public sealed class Other
            {
                public Task WriteAsync() => Task.CompletedTask;
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create("Snippet", [tree], References.Value,
            new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers([new ChattyAwaitAnalyzer()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. diagnostics.OrderBy(d => d.Location.SourceSpan.Start).Select(d => new Found(d.Id, d.Severity, Line(d)))];

        static string Line(Diagnostic d) => d.Location.SourceTree!.GetText().Lines.GetLineFromPosition(d.Location.SourceSpan.Start).ToString().Trim();
    }

    // The test's own platform assemblies, which include the runtime and the golden slice.
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JsObject).Assembly.Location).Append(typeof(GoldenSlice.Async.MapView).Assembly.Location)
            .Distinct().Select(p => MetadataReference.CreateFromFile(p))]);
}
