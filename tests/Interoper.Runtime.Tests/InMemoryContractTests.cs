using Interoper.Contract;

namespace Interoper.Runtime.Tests;

/// <summary>The transport contract against <see cref="InMemoryTransport"/>, through both seams.</summary>
public sealed class InMemoryContractTests
{
    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var (name, _) in TransportContract.Cases)
        {
            data.Add(name, true);
            data.Add(name, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Passes(string name, bool sync)
    {
        var transport = InMemoryDemoMaps.Create();
        var seam = sync ? Seam.Sync(transport) : Seam.Async(transport, callbacksReturn: true);
        return TransportContract.Cases.Single(c => c.Name == name).Run(seam);
    }

    [Fact]
    public Task TwoTransportsAreIndependent() =>
        TransportContract.Independent(Seam.Sync(InMemoryDemoMaps.Create()), Seam.Async(InMemoryDemoMaps.Create(), callbacksReturn: true));
}
