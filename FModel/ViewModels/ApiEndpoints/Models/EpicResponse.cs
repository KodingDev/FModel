using System;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace FModel.ViewModels.ApiEndpoints.Models;

[DebuggerDisplay("{" + nameof(AccessToken) + "}")]
public class AuthResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; }
    [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
}
