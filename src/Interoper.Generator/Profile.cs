using System.Text.Json;

namespace Interoper.Generator;

/// <summary>
/// The consumer's library-specific decisions (design §4), from <c>profile.json</c>. An unknown field is an error.
/// </summary>
public sealed record Profile
{
    /// <summary>The npm package the profile applies to; it must be the IR's.</summary>
    public required string Package { get; init; }

    /// <summary>The instance property holding the runtime class name (R19 step 1); never bound as a member.</summary>
    public string? RuntimeClassProperty { get; init; }

    /// <summary>Classes that also get an <c>XSnapshot</c> record used by value (R16).</summary>
    public IReadOnlyList<string> ValueClasses { get; init; } = [];

    public ProfileEvents? Events { get; init; }

    /// <summary>The method that takes <c>(propertyName, callback)</c> and returns a subscription.</summary>
    public string? Watch { get; init; }

    /// <summary>Generic classes treated as collections of their type argument.</summary>
    public IReadOnlyList<string> Collections { get; init; } = [];

    /// <summary>
    /// What to emit; absent means everything. <c>"Point"</c> is a whole declaration, <c>"MapView.zoom"</c> one member,
    /// <c>"MapView.on:click"</c> one event and <c>"MapView.watch:zoom"</c> one property watch (<see cref="Selection"/>).
    /// </summary>
    public IReadOnlyList<string>? Include { get; init; }

    public static Profile Load(string path) => Parse(File.ReadAllText(path));

    public static Profile Parse(string json) => JsonSerializer.Deserialize<Profile>(json, Json.Options)!;
}

/// <param name="MapProperty">The phantom event-map property (R24); the extractor reads it.</param>
/// <param name="Subscribe">The method that takes <c>(type, listener)</c> and returns a subscription.</param>
public sealed record ProfileEvents(string? MapProperty = null, string? Subscribe = null);
