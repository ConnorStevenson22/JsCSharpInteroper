namespace Interoper.Generator;

/// <summary>A fallback the generator reports (mapping §8): <c>IOP2007 MapView.flyTo(options): …</c>.</summary>
/// <param name="Subject">What it's about: a declaration, <c>Type.member</c>, or <c>Type.method(parameter)</c>.</param>
public sealed record Diagnostic(string Code, string Subject, string Message)
{
    /// <summary>A name changed to avoid a collision.</summary>
    public const string Renamed = "IOP2006";

    /// <summary>Not supported yet: a declaration, member or optional parameter skipped.</summary>
    public const string NotSupported = "IOP2007";

    public override string ToString() => $"{Code} {Subject}: {Message}";
}
