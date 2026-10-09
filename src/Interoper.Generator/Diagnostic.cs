namespace Interoper.Generator;

/// <summary>A fallback the generator reports (mapping §8): <c>IOP2007 MapView.flyTo(options): …</c>.</summary>
/// <param name="Subject">What it's about: a declaration, <c>Type.member</c>, or <c>Type.method(parameter)</c>.</param>
public sealed record Diagnostic(string Code, string Subject, string Message)
{
    /// <summary><c>any</c> / <c>unknown</c> / <c>object</c> mapped to <c>JsAny</c>.</summary>
    public const string Any = "IOP2001";

    /// <summary>A type the checker couldn't resolve, mapped to <c>JsAny</c>.</summary>
    public const string Unresolved = "IOP2002";

    /// <summary>An ambiguous union with no generated classifier.</summary>
    public const string AmbiguousUnion = "IOP2003";

    /// <summary>Overloads merged after mapping.</summary>
    public const string OverloadsMerged = "IOP2004";

    /// <summary>A synchronous-result callback in async mode.</summary>
    public const string SyncCallback = "IOP2005";

    /// <summary>A name changed to avoid a collision.</summary>
    public const string Renamed = "IOP2006";

    /// <summary>Not supported yet: a declaration, member or optional parameter skipped.</summary>
    public const string NotSupported = "IOP2007";

    public override string ToString() => $"{Code} {Subject}: {Message}";
}
