using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Versions;
using FModel.Creator;
using FModel.Extensions;
using SkiaSharp;

namespace FModel.ViewModels.ApiEndpoints.Models;

[DebuggerDisplay("{" + nameof(Messages) + "}")]
public class News
{
    [JsonPropertyName("messages")] public string[] Messages { get; private set; }
    [JsonPropertyName("colors")] public string[] Colors { get; private set; }
    [JsonPropertyName("newLines")] public string[] NewLines { get; private set; }
}

[DebuggerDisplay("{" + nameof(FileName) + "}")]
public class Backup
{
    [JsonPropertyName("gameName")] public string GameName { get; private set; }
    [JsonPropertyName("fileName")] public string FileName { get; private set; }
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; private set; }
    [JsonPropertyName("fileSize")] public long FileSize { get; private set; }
}

public class Donator
{
    [JsonPropertyName("username")] public string Username { get; private set; }
    [JsonPropertyName("count")] public int Count { get; private set; }

    public override string ToString() => $"{Username}{(Count > 5 ? " ❤️" : "")}";
}

[DebuggerDisplay("{" + nameof(DisplayName) + "}")]
public class Game
{
    [JsonPropertyName("displayName")] public string DisplayName { get; private set; }
    [JsonPropertyName("versions")] public Dictionary<string, Version> Versions { get; private set; }
}

[DebuggerDisplay("{" + nameof(GameEnum) + "}")]
public class Version
{
    [JsonPropertyName("game")] public string GameEnum { get; private set; }
    [JsonPropertyName("ueVer")] public int UeVer { get; private set; }
    [JsonPropertyName("customVersions")] public Dictionary<string, int> CustomVersions { get; private set; }
    [JsonPropertyName("options")] public Dictionary<string, bool> Options { get; private set; }
    [JsonPropertyName("mapStructTypes")] public Dictionary<string, KeyValuePair<string, string>> MapStructTypes { get; private set; } = new();
}

[DebuggerDisplay("{" + nameof(Mode) + "}")]
public class Info
{
    [JsonPropertyName("mode")] public string Mode { get; private set; }
    [JsonPropertyName("version")] public string Version { get; private set; }
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; private set; }
    [JsonPropertyName("changelogUrl")] public string ChangelogUrl { get; private set; }
    [JsonPropertyName("communityDesign")] public string CommunityDesign { get; private set; }
    [JsonPropertyName("communityPreview")] public string CommunityPreview { get; private set; }
}

[DebuggerDisplay("{" + nameof(Name) + "}")]
public class Community
{
    [JsonPropertyName("name")] public string Name { get; private set; }
    [JsonPropertyName("drawSource")] public bool DrawSource { get; private set; }
    [JsonPropertyName("drawSeason")] public bool DrawSeason { get; private set; }
    [JsonPropertyName("drawSeasonShort")] public bool DrawSeasonShort { get; private set; }
    [JsonPropertyName("drawSet")] public bool DrawSet { get; private set; }
    [JsonPropertyName("drawSetShort")] public bool DrawSetShort { get; private set; }
    [JsonPropertyName("fonts")] public IDictionary<string, Font> Fonts { get; private set; }
    [JsonPropertyName("gameplayTags")] public GameplayTag GameplayTags { get; private set; }
    [JsonPropertyName("rarities")] public IDictionary<string, Rarity> Rarities { get; private set; }
}

public class Font
{
    [JsonPropertyName("typeface")] public IDictionary<string, string> Typeface { get; private set; }
    [JsonPropertyName("fontSize")] public float FontSize { get; private set; }
    [JsonPropertyName("fontScale")] public float FontScale { get; private set; }
    [JsonPropertyName("fontColor")] public string FontColor { get; private set; }
    [JsonPropertyName("skewValue")] public float SkewValue { get; private set; }
    [JsonPropertyName("shadowValue")] public byte ShadowValue { get; private set; }
    [JsonPropertyName("maxLineCount")] public int MaxLineCount { get; private set; }
    [JsonPropertyName("alignment")] public string Alignment { get; private set; }
    [JsonPropertyName("x")] public int X { get; private set; }
    [JsonPropertyName("y")] public int Y { get; private set; }
}

public class FontDesign
{
    [JsonPropertyName("typeface")] public IDictionary<ELanguage, string> Typeface { get; set; }
    [JsonPropertyName("fontSize")] public float FontSize { get; set; }
    [JsonPropertyName("fontScale")] public float FontScale { get; set; }
    [JsonPropertyName("fontColor")] public SKColor FontColor { get; set; }
    [JsonPropertyName("skewValue")] public float SkewValue { get; set; }
    [JsonPropertyName("shadowValue")] public byte ShadowValue { get; set; }
    [JsonPropertyName("maxLineCount")] public int MaxLineCount { get; set; }
    [JsonPropertyName("alignment")] public SKTextAlign Alignment { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
}

public class GameplayTag
{
    [JsonPropertyName("x")] public int X { get; private set; }
    [JsonPropertyName("y")] public int Y { get; private set; }
    [JsonPropertyName("drawCustomOnly")] public bool DrawCustomOnly { get; private set; }
    [JsonPropertyName("custom")] public string Custom { get; private set; }
    [JsonPropertyName("tags")] public IDictionary<string, string> Tags { get; private set; }
}

public class GameplayTagDesign
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("drawCustomOnly")] public bool DrawCustomOnly { get; set; }
    [JsonPropertyName("custom")] public SKBitmap Custom { get; set; }
    [JsonPropertyName("tags")] public IDictionary<string, SKBitmap> Tags { get; set; }
}

public class Rarity
{
    [JsonPropertyName("background")] public string Background { get; private set; }
    [JsonPropertyName("upper")] public string Upper { get; private set; }
    [JsonPropertyName("lower")] public string Lower { get; private set; }
}

public class RarityDesign
{
    [JsonPropertyName("background")] public SKBitmap Background { get; set; }
    [JsonPropertyName("upper")] public SKBitmap Upper { get; set; }
    [JsonPropertyName("lower")] public SKBitmap Lower { get; set; }
}

public class CommunityDesign
{
    public bool DrawSource { get; }
    public bool DrawSeason { get; }
    public bool DrawSeasonShort { get; }
    public bool DrawSet { get; }
    public bool DrawSetShort { get; }
    public IDictionary<string, FontDesign> Fonts { get; }
    public GameplayTagDesign GameplayTags { get; }
    public IDictionary<string, RarityDesign> Rarities { get; }

    public CommunityDesign(Community response)
    {
        DrawSource = response.DrawSource;
        DrawSeason = response.DrawSeason;
        DrawSeasonShort = response.DrawSeasonShort;
        DrawSet = response.DrawSet;
        DrawSetShort = response.DrawSetShort;

        Fonts = new Dictionary<string, FontDesign>();
        foreach (var (k, font) in response.Fonts)
        {
            var typeface = new Dictionary<ELanguage, string>();
            foreach (var (key, value) in font.Typeface)
            {
                typeface[key.ToEnum(ELanguage.English)] = value;
            }

            Fonts[k] = new FontDesign
            {
                Typeface = typeface,
                FontSize = font.FontSize,
                FontScale = font.FontScale,
                FontColor = SKColor.Parse(font.FontColor),
                SkewValue = font.SkewValue,
                ShadowValue = font.ShadowValue,
                MaxLineCount = font.MaxLineCount,
                Alignment = font.Alignment.ToEnum(SKTextAlign.Center),
                X = font.X,
                Y = font.Y
            };
        }

        var tags = new Dictionary<string, SKBitmap>();
        foreach (var (key, value) in response.GameplayTags.Tags)
        {
            tags[key] = Utils.GetB64Bitmap(value);
        }

        GameplayTags = new GameplayTagDesign
        {
            X = response.GameplayTags.X,
            Y = response.GameplayTags.Y,
            DrawCustomOnly = response.GameplayTags.DrawCustomOnly,
            Custom = Utils.GetB64Bitmap(response.GameplayTags.Custom),
            Tags = tags
        };

        Rarities = new Dictionary<string, RarityDesign>();
        foreach (var (key, value) in response.Rarities)
        {
            Rarities[key] = new RarityDesign
            {
                Background = Utils.GetB64Bitmap(value.Background),
                Upper = Utils.GetB64Bitmap(value.Upper),
                Lower = Utils.GetB64Bitmap(value.Lower)
            };
        }
    }
}
