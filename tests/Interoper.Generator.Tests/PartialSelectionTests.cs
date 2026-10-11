using Interoper.Generator.Ir;

namespace Interoper.Generator.Tests;

/// <summary>
/// Mapping §8: the generator never throws for input it can't map, whatever the profile selects. A partial selection
/// leaves classes out, so unions drop cases (R2), members are skipped and bases come in: each module of the fixtures
/// on its own and every pair of them, in both modes, generates without an exception.
/// </summary>
public sealed class PartialSelectionTests
{
    public static TheoryData<string, Api> Fixtures() => new()
    {
        { "demo-maps", Api.Sync }, { "demo-maps", Api.Async }, { "demo-maps+demo-widgets", Api.Sync }, { "demo-maps+demo-widgets", Api.Async },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryModuleAndEveryPairOfModulesGenerates(string fixture, Api api)
    {
        var ir = fixture == "demo-maps" ? Fixture.Ir : Fixture.WithWidgetsIr;
        var modules = ir.Modules.Select(m => m.Specifier).ToList();
        var includes = modules.Select(m => new[] { m })
            .Concat(modules.SelectMany((a, i) => modules.Skip(i + 1).Select(b => new[] { a, b })));
        var failures = new List<string>();
        foreach (var include in includes)
        {
            try
            {
                var result = Generator.Generate(ir, Fixture.Profile with { Include = include }, new(api, "Acme.Maps"));
                Assert.NotEmpty(result.Files);
            }
            catch (Exception e) when (e is not InvalidDataException)
            {
                failures.Add($"{string.Join(" + ", include)}: {e.GetType().Name}: {e.Message}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
