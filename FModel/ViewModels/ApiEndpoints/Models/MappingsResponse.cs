using System.Diagnostics;
using System.Text.Json.Serialization;

namespace FModel.ViewModels.ApiEndpoints.Models;

[DebuggerDisplay("{" + nameof(FileName) + "}")]
public class MappingsResponse
{
    [JsonPropertyName("url")] public string Url { get; set; }
    [JsonPropertyName("fileName")] public string FileName { get; set; }
    [JsonIgnore][JsonPropertyName("hash")] public string Hash { get; private set; }
    [JsonIgnore][JsonPropertyName("length")] public long Length { get; private set; }
    [JsonIgnore][JsonPropertyName("uploaded")] public string Uploaded { get; private set; }
    [JsonIgnore][JsonPropertyName("meta")] public Meta Meta { get; set; }

    public MappingsResponse()
    {
        Url = string.Empty;
        FileName = string.Empty;
    }

    [JsonIgnore] public bool IsValid => !string.IsNullOrEmpty(Url) &&
                              !string.IsNullOrEmpty(FileName);
}

[DebuggerDisplay("{" + nameof(CompressionMethod) + "}")]
public class Meta
{
    [JsonIgnore][JsonPropertyName("version")] public string Version { get; private set; }
    [JsonPropertyName("compressionMethod")] public string CompressionMethod { get; set; }
}
