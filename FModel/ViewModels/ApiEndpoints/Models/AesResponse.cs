using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace FModel.ViewModels.ApiEndpoints.Models;

[DebuggerDisplay("{" + nameof(Version) + "}")]
public class AesResponse
{
    [JsonIgnore][JsonPropertyName("version")] public string Version { get; private set; }
    [JsonPropertyName("mainKey")] public string MainKey { get; set; }
    [JsonPropertyName("dynamicKeys")] public List<DynamicKey> DynamicKeys { get; set; }

    public AesResponse()
    {
        MainKey = string.Empty;
        DynamicKeys = new List<DynamicKey>();
    }

    [JsonIgnore] public bool HasDynamicKeys => DynamicKeys is { Count: > 0 };
    [JsonIgnore] public bool IsValid => MainKey.Length == 66 || HasDynamicKeys;
}

[DebuggerDisplay("{" + nameof(Key) + "}")]
public class DynamicKey
{
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("guid")] public string Guid { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; }

    [JsonIgnore] public bool IsValid => Guid.Length == 32 && Key.Length == 66;
}
