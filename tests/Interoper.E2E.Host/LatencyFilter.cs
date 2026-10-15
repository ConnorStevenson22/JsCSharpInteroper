using Microsoft.AspNetCore.SignalR;

namespace Interoper.E2E.Host;

/// <summary>
/// Delays every hub invocation from the browser, which on Blazor Server is every JS interop reply, event and callback,
/// so each round trip costs at least the delay (design §6, LatencyTests). SignalR runs one client's invocations one at a
/// time, so they keep their order, as on a real connection, and queue behind each other: a slow link, not only a far one.
/// </summary>
internal sealed class LatencyFilter(TimeSpan delay) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await Task.Delay(delay);
        return await next(context);
    }
}
