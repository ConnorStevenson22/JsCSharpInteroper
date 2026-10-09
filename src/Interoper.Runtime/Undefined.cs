namespace Interoper;

/// <summary>
/// JavaScript's <c>undefined</c> (R1). Users can't construct it; the runtime owns the one instance.
/// It is the bottom case of every generated union and of <see cref="Opt{T}"/>, and their <c>default</c>.
/// </summary>
public sealed class Undefined
{
    public static readonly Undefined Value = new();

    private Undefined() { }

    public override string ToString() => "undefined";
}
