using System.Runtime.CompilerServices;

namespace Interoper;

/// <summary>
/// A non-union type in an optional or nullable position (R3), e.g. <c>width?: number | null</c>.
/// <c>default</c> is undefined, the <c>null</c> literal is JS null, anything else is a value. No boxing.
/// </summary>
[Union]
public readonly struct Opt<T> : IUnion, IEquatable<Opt<T>> where T : notnull
{
    private readonly T _value;
    private readonly byte _tag; // 0 = undefined, 1 = null, 2 = value

    private Opt(T value, byte tag) => (_value, _tag) = (value, tag);

    public Opt(T value) : this(value, 2) { }

    /// <summary>The bottom case: a null reference is JS null, the singleton is undefined.</summary>
    public Opt(Undefined? value) : this(default!, value is null ? (byte)1 : (byte)0) { }

    /// <summary>Lets the <c>null</c> literal compile: union conversions never apply to <c>null</c>.</summary>
    public static implicit operator Opt<T>(Undefined? value) => new(value);

    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _value };
    public bool HasValue => _tag != 1;
    public bool IsUndefined => _tag == 0;
    public bool IsNull => _tag == 1;

    public bool TryGetValue(out T value)
    {
        value = _value;
        return _tag == 2;
    }

    public bool TryGetValue(out Undefined value)
    {
        value = Undefined.Value;
        return _tag == 0;
    }

    public bool Equals(Opt<T> other) => _tag == other._tag && EqualityComparer<T>.Default.Equals(_value, other._value);
    public override bool Equals(object? obj) => obj is Opt<T> other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_tag, _value);
    public override string ToString() => _tag switch { 0 => "undefined", 1 => "null", _ => $"{_value}" };
}
