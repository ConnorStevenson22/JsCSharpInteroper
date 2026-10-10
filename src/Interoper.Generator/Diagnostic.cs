namespace Interoper.Generator;

/// <summary>A fallback the generator reports (mapping §8): <c>IOP2007 MapView.flyTo(options): …</c>.</summary>
/// <param name="Subject">What it's about: a declaration, <c>Type.member</c>, or <c>Type.method(parameter)</c>.</param>
public sealed record Diagnostic(string Code, string Subject, string Message)
{
    /// <summary><c>any</c>, <c>unknown</c> or <c>object</c> mapped to <c>JsAny</c> (mapping §2).</summary>
    public const string Untyped = "IOP2001";

    /// <summary>An ambiguous union skipped: its cases can't be told apart (R19).</summary>
    public const string Ambiguous = "IOP2003";

    /// <summary>Skipped in async mode: a callback whose result JS uses (R21).</summary>
    public const string SyncOnly = "IOP2005";

    /// <summary>A name changed to avoid a collision.</summary>
    public const string Renamed = "IOP2006";

    /// <summary>Skipped: a declaration, member or optional parameter the generator can't map (yet).</summary>
    public const string NotSupported = "IOP2007";

    public override string ToString() => $"{Code} {Subject}: {Message}";
}

/// <summary>Why a type or declaration can't be mapped, and the code a skip reports it with.</summary>
internal sealed record Unmappable(string Text, string Code = Diagnostic.NotSupported)
{
    public override string ToString() => Text;
}
