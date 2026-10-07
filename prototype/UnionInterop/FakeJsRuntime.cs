using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;

namespace UnionInterop;

/// <summary>
/// A JSRuntime with no browser behind it. Calls go through the real Microsoft.JSInterop
/// serializer (the one Blazor uses), the outgoing argument JSON is recorded, and the reply
/// is fed back through <see cref="DotNetDispatcher.EndInvokeJS"/> exactly as blazor.*.js would.
/// </summary>
public sealed class FakeJsRuntime : JSRuntime
{
    public Func<string, string?, string> Reply { get; set; } = (_, _) => "null";
    public List<(string Identifier, string? ArgsJson)> Calls { get; } = [];

    protected override void BeginInvokeJS(long taskId, string identifier, string? argsJson, JSCallResultType resultType, long targetInstanceId)
    {
        Calls.Add((identifier, argsJson));
        var result = Reply(identifier, argsJson);
        DotNetDispatcher.EndInvokeJS(this, $"[{taskId},true,{result}]");
    }

    protected override void EndInvokeDotNet(DotNetInvocationInfo invocationInfo, in DotNetInvocationResult invocationResult) =>
        throw new NotSupportedException();
}
