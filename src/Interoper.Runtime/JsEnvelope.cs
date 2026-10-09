using System.Text;
using System.Text.Json;

namespace Interoper;

/// <summary>
/// Reads the envelope an op returns (design §3.4): <c>{"v": result}</c>, <c>{}</c> for undefined, or
/// <c>{"e": {"name", "message", "stack"}}</c>, which throws <see cref="JsException"/>.
/// </summary>
public static class JsEnvelope
{
    /// <summary>Throws if the op failed.</summary>
    public static void Check(string envelope) => Read(envelope, static (ref _) => 0);

    /// <summary>The result, read by <paramref name="read"/>; <c>default</c> (undefined) when there is none.</summary>
    public static T? Read<T>(string envelope, JsonRead<T> read)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(envelope));
        reader.Read();
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndObject)
        {
            return default;
        }
        var isValue = reader.ValueTextEquals("v"u8);
        reader.Read();
        return isValue ? read(ref reader) : throw ReadError(ref reader);
    }

    /// <summary>The exception in an error envelope (<c>takeError</c>'s, after a fast path failed).</summary>
    internal static JsException Error(string envelope)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(envelope));
        reader.Read(); // {
        reader.Read(); // "e"
        reader.Read(); // {
        return ReadError(ref reader);
    }

    // A batch's error also carries "errors": one entry per failed op, with its index.
    private static JsException ReadError(ref Utf8JsonReader reader) => ReadError(ref reader, out _);

    private static JsException ReadError(ref Utf8JsonReader reader, out int index)
    {
        string name = "Error", message = "";
        string? stack = null;
        List<(int, JsException)>? errors = null;
        index = -1;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "name": name = reader.GetString() ?? name; break;
                case "message": message = reader.GetString() ?? ""; break;
                case "stack": stack = reader.GetString(); break;
                case "index": index = reader.GetInt32(); break;
                case "errors":
                    errors = [];
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        var error = ReadError(ref reader, out var i);
                        errors.Add((i, error));
                    }
                    break;
                default: reader.Skip(); break;
            }
        }
        return errors is null ? new JsException(name, message, stack) : new JsBatchException(message, stack, errors);
    }
}
