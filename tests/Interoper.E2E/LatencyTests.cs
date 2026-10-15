using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Interoper.Contract;

namespace Interoper.E2E;

/// <summary>
/// Server under latency (design §3.3, §6): a second test host whose SignalR filter delays every message from the
/// browser by <see cref="LatencyHost.Latency"/>, so each round trip costs at least that. The async golden suite and the
/// transport contract still pass, and a batch of 100 writes is one round trip within three latencies.
/// </summary>
public sealed partial class LatencyTests(BrowserHost browser, LatencyHost host) : IClassFixture<LatencyHost>
{
    // Hundreds of round trips at 50 ms or more each: about 30 s for the golden suite on a busy machine.
    private static readonly TimeSpan SuiteTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task TheGoldenSuitePassesUnderLatency()
    {
        var output = await browser.RunPageAsync("golden-server", host.BaseUrl, SuiteTimeout);
        Assert.Equal(GoldenAsyncSuite.Cases.Select(c => $"PASS {c.Name}"), output.Split('\n'));
    }

    [Fact]
    public async Task TheContractPassesUnderLatency()
    {
        var output = await browser.RunPageAsync("server", host.BaseUrl, SuiteTimeout);
        Assert.Equal(TransportContract.Cases.Select(c => $"PASS {c.Name}"), output.Split('\n'));
    }

    // One round trip pays the latency once, so a batch takes at least it. The bound, three latencies, leaves two (100 ms)
    // for what isn't latency: 100 writes serialised, sent and run in JS, and a busy machine's scheduling. Sending the
    // writes one by one would take at least 100 latencies; a second round trip would still fit the bound, which is why
    // the round-trip counter is checked too.
    [Fact]
    public async Task ABatchOf100WritesIsOneRoundTrip()
    {
        var output = await browser.RunPageAsync("server-latency-batch", host.BaseUrl);
        var match = Timings().Match(output);
        Assert.True(match.Success, output);
        var (read, batch) = (Milliseconds(match, "read"), Milliseconds(match, "batch"));
        Assert.Equal("1", match.Groups["trips"].Value);
        Assert.Equal("19", match.Groups["zoom"].Value); // the last write
        Assert.InRange(read, LatencyHost.Latency.TotalMilliseconds - 1, double.MaxValue); // the delay is on (less 1 for rounding)
        Assert.InRange(batch, LatencyHost.Latency.TotalMilliseconds - 1, 3 * LatencyHost.Latency.TotalMilliseconds);
        TestContext.Current.TestOutputHelper?.WriteLine(output);
    }

    private static double Milliseconds(Match match, string group) => double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^read (?<read>\d+) ms, batch of 100 (?<batch>\d+) ms in (?<trips>\d+) round trip\(s\), zoom (?<zoom>\d+)$")]
    private static partial Regex Timings();
}

/// <summary>The test host, started on its own with a <see cref="Latency"/> on every message from the browser.</summary>
public sealed class LatencyHost : IAsyncLifetime
{
    public static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(50);

    private Process? process;

    public Uri BaseUrl { get; private set; } = null!;

    public async ValueTask InitializeAsync() =>
        (process, BaseUrl) = await BrowserHost.StartHostAsync($"--TestHost:LatencyMs={Latency.TotalMilliseconds}");

    public async ValueTask DisposeAsync() => await BrowserHost.StopAsync(process);
}
