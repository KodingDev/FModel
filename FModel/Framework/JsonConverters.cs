using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;

namespace FModel.Framework;

public class GridLengthConverter : JsonConverter<GridLength>
{
    public override GridLength Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (double.TryParse(value, out var doubleValue))
                return new GridLength(doubleValue);
            return new GridLength(200); // default
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            return new GridLength(reader.GetDouble());
        }
        return new GridLength(200);
    }

    public override void Write(Utf8JsonWriter writer, GridLength value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

public class HotkeyConverter : JsonConverter<Hotkey>
{
    public override Hotkey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return new Hotkey(Key.None);

        Key key = Key.None;
        ModifierKeys modifiers = ModifierKeys.None;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString();
                reader.Read();

                if (propertyName?.Equals("Key", StringComparison.OrdinalIgnoreCase) == true)
                {
                    key = (Key)reader.GetInt32();
                }
                else if (propertyName?.Equals("Modifiers", StringComparison.OrdinalIgnoreCase) == true)
                {
                    modifiers = (ModifierKeys)reader.GetInt32();
                }
            }
        }

        return new Hotkey(key, modifiers);
    }

    public override void Write(Utf8JsonWriter writer, Hotkey value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Key", (int)value.Key);
        writer.WriteNumber("Modifiers", (int)value.Modifiers);
        writer.WriteEndObject();
    }
}

