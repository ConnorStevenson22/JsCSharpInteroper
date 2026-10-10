using GoldenSlice.Sync;

namespace Interoper.Runtime.Tests;

/// <summary><see cref="JsDiff"/> and the generated <c>XSnapshot.Diff</c> (R18); the suites apply the patches.</summary>
public sealed class JsDiffTests
{
    [Fact] // R18
    public void ArraysCompareElementByElementRecursively()
    {
        Assert.True(JsDiff.Same(new double[][] { [0, 1], [2, 3] }, [[0, 1], [2, 3]]));
        Assert.False(JsDiff.Same(new double[][] { [0, 1], [2, 3] }, [[0, 1], [2, 4]]));
        Assert.False(JsDiff.Same(new double[][] { [0, 1] }, [[0, 1], [2, 3]]));
        Assert.True(JsDiff.Same(new string[] { "a" }, ["a"]));
    }

    [Fact] // R2, R3, R18: inside an Opt or a union case too, and its three states stay apart
    public void OptsAndUnionsCompareTheirValues()
    {
        Assert.True(JsDiff.Same<Opt<double[]>>(new double[] { 1, 2 }, new double[] { 1, 2 }));
        Assert.False(JsDiff.Same<Opt<double[]>>(new double[] { 1, 2 }, null));
        Assert.False(JsDiff.Same<Opt<double[]>>(default, null));
        Assert.True(JsDiff.Same<Opt<double[]>>(null, null));
        Assert.True(JsDiff.Same<ColorInput>(new double[] { 1, 2, 3 }, new double[] { 1, 2, 3 }));
        Assert.False(JsDiff.Same<ColorInput>(new double[] { 1, 2, 3 }, "red"));
        Assert.False(JsDiff.Same<ColorInput>(default, null));
    }

    [Fact] // R18: a record uses its own equality, so an array inside one compares by reference
    public void RecordsUseTheirOwnEquality()
    {
        Assert.True(JsDiff.Same(new PointSnapshot { X = 1, Y = 2 }, new PointSnapshot { X = 1, Y = 2 }));
        Assert.True(JsDiff.Same<Style>(new FillStyle { Color = "red" }, new FillStyle { Color = "red" }));
        Assert.False(JsDiff.Same<Style>(new StrokeStyle { Color = new double[] { 1, 2, 3 } }, new StrokeStyle { Color = new double[] { 1, 2, 3 } }));
    }

    [Fact] // R18
    public void ThePatchHasWhatAfterDefinesAndChanges()
    {
        var before = new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 3, Title = "a", Basemap = Basemap.Night };
        var after = new MapViewSnapshot { Center = new PointSnapshot { X = 1, Y = 2 }, Zoom = 4, Title = null, Rotation = 90 };
        // The center is the same (nested, by value); the basemap is unset in after, so it stays out: a patch can't unset.
        Assert.Equal(new MapViewSnapshot { Zoom = 4, Title = null, Rotation = 90 }, MapViewSnapshot.Diff(before, after));
        Assert.Equal(new MapViewSnapshot(), MapViewSnapshot.Diff(after, after));
        Assert.Equal(new MapViewSnapshot(), MapViewSnapshot.Diff(before, new MapViewSnapshot()));
        Assert.Equal(new EmitterSnapshot(), EmitterSnapshot.Diff(new(), new()));
    }

    [Fact] // R16, R18: a value-like subclass's snapshot has a Diff; the closed base's doesn't, as its subclass decides
    public void AValueLikeSnapshotDiffs()
    {
        var before = new LineSnapshot { Path = new double[][] { [0, 1], [2, 3] } };
        Assert.Equal(new LineSnapshot(), LineSnapshot.Diff(before, new LineSnapshot { Path = new double[][] { [0, 1], [2, 3] } }));
        var patch = LineSnapshot.Diff(before, new LineSnapshot { Path = new double[][] { [0, 1] } });
        Assert.True(patch.Path.TryGetValue(out double[][]? path) && path.Length == 1);
        Assert.Null(typeof(ShapeSnapshot).GetMethod("Diff"));
    }
}
