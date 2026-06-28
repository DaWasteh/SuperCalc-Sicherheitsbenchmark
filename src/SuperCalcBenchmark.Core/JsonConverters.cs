using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public sealed class AliasListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var aliases = new List<string>();
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    AddAlias(aliases, reader.GetString());
                }
            }

            return aliases;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            break;
                        }

                        if (reader.TokenType == JsonTokenType.String)
                        {
                            AddAlias(aliases, reader.GetString());
                        }
                    }
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    AddAlias(aliases, reader.GetString());
                }
                else
                {
                    reader.Skip();
                }
            }

            return aliases;
        }

        reader.Skip();
        return aliases;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var alias in value ?? [])
        {
            writer.WriteStringValue(alias);
        }
        writer.WriteEndArray();
    }

    private static void AddAlias(List<string> aliases, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !aliases.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            aliases.Add(value);
        }
    }
}
