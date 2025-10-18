using System;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace FModel.ViewModels.ApiEndpoints.Models;

[DebuggerDisplay("{" + nameof(DebuggerDisplay) + "}")]
public class PlaylistResponse
{
    [JsonPropertyName("status")] public int Status { get; private set; }
    [JsonPropertyName("data")] public Playlist Data { get; private set; }
    [JsonPropertyName("error")] public string Error { get; private set; }

    [JsonIgnore] public bool IsSuccess => Status == 200;
    [JsonIgnore] public bool HasError => Error != null;
    [JsonIgnore] private object DebuggerDisplay => IsSuccess ? Data : $"Error: {Status} | {Error}";
}

[DebuggerDisplay("{" + nameof(Id) + "}")]
public class Playlist
{
    [JsonPropertyName("id")] public string Id { get; private set; }
    [JsonPropertyName("images")] public PlaylistImages Images { get; private set; }
}

public class PlaylistImages
{
    [JsonPropertyName("showcase")] public Uri Showcase { get; private set; }
    [JsonPropertyName("missionIcon")] public Uri MissionIcon { get; private set; }

    [JsonIgnore] public bool HasShowcase => Showcase != null;
    [JsonIgnore] public bool HasMissionIcon => MissionIcon != null;
}
