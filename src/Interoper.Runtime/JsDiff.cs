using System.Runtime.CompilerServices; // IUnion

namespace Interoper;

/// <summary>The comparison generated <c>XSnapshot.Diff</c> methods use (R18).</summary>
public static class JsDiff
{
    /// <summary>
    /// Whether two member values are the same: by <see cref="EqualityComparer{T}"/>, except that arrays compare element
    /// by element, recursively, also inside an <see cref="Opt{T}"/> or a union case. Other values, records included,
    /// use their own equality, so an array inside a nested record compares by reference.
    /// </summary>
    public static bool Same<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b) || Deep(a, b);

    private static bool Deep(object? a, object? b) => (a, b) switch
    {
        (IUnion x, IUnion y) => Deep(x.Value, y.Value),
        (Array x, Array y) => x.Length == y.Length && Enumerable.Range(0, x.Length).All(i => Deep(x.GetValue(i), y.GetValue(i))),
        _ => Equals(a, b),
    };
}
