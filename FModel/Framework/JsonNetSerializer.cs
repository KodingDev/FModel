using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using RestSharp;
using RestSharp.Serializers;

namespace FModel.Framework;

public class JsonNetSerializer : IRestSerializer, ISerializer, IDeserializer
{
    public static readonly JsonSerializerOptions SerializerSettings = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null, // Use property names as-is (PascalCase)
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new GridLengthConverter(),
            new HotkeyConverter()
        }
    };

    public string Serialize(Parameter parameter) => JsonSerializer.Serialize(parameter.Value, SerializerSettings);
    public string Serialize(object obj) => JsonSerializer.Serialize(obj, SerializerSettings);
    public T Deserialize<T>(RestResponse response) => JsonSerializer.Deserialize<T>(response.Content!, SerializerSettings);

    public ISerializer Serializer => this;
    public IDeserializer Deserializer => this;

    public ContentType ContentType { get; set; } = ContentType.Json;
    public string[] AcceptedContentTypes => ContentType.JsonAccept;
    public SupportsContentType SupportsContentType => contentType => contentType.Value.EndsWith("json", StringComparison.InvariantCultureIgnoreCase);

    public DataFormat DataFormat => DataFormat.Json;
}
