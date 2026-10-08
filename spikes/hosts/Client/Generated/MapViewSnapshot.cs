// Data types are identical in both generation modes; only live members differ.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostsSpike.Client.Generated;

public sealed record MapViewSnapshot
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Zoom { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Rotation { get; init; }
}

public sealed record ClickEvent(double X, double Y);

internal static class GeneratedJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
