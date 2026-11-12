using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyLibrarian.Web.Client.Settings;

/// <summary>Tracks independently saved sections against their last successful load or save.</summary>
public sealed class SettingsSnapshot
{
    private readonly Dictionary<string, string> saved = [];
    private static readonly JsonSerializerOptions Options = new() { Converters = { new EmptyStringConverter() } };

    public void Accept(string section, object value) => saved[section] = JsonSerializer.Serialize(value, Options);

    public bool HasChanges(string section, object value) =>
        saved.TryGetValue(section, out var snapshot) && snapshot != JsonSerializer.Serialize(value, Options);

    // Text inputs can emit null after clearing a field originally loaded as an empty string.
    private sealed class EmptyStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value.Length == 0) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }
    }
}
