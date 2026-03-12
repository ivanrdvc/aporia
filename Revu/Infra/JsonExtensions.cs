using System.Text.Json;

namespace Revu.Infra;

public static class JsonExtensions
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { PropertyNameCaseInsensitive = true };

    public static T? TryParseJson<T>(this string? text, JsonSerializerOptions? options = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(text, options ?? DefaultOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
