using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using FModel.Extensions;
using ICSharpCode.AvalonEdit.Document;

namespace FModel.Views.Resources.Controls;

public partial class DictionaryEditor
{
    private readonly List<FCustomVersion> _defaultCustomVersions;
    private readonly Dictionary<string, bool> _defaultOptions;
    private readonly Dictionary<string, KeyValuePair<string, string>> _defaultMapStructTypes;

    public List<FCustomVersion> CustomVersions { get; private set; }
    public Dictionary<string, bool> Options { get; private set; }
    public Dictionary<string, KeyValuePair<string, string>> MapStructTypes { get; private set; }

    public DictionaryEditor(string title)
    {
        _defaultCustomVersions = new List<FCustomVersion> { new() { Key = new FGuid(), Version = 0 } };
        _defaultOptions = new Dictionary<string, bool> { { "key1", true }, { "key2", false } };
        _defaultMapStructTypes = new Dictionary<string, KeyValuePair<string, string>> { { "MapName", new KeyValuePair<string, string>("KeyType", "ValueType") } };

        InitializeComponent();

        Title = title;
        MyAvalonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("");
    }

    public DictionaryEditor(IList<FCustomVersion> customVersions, string title) : this(title)
    {
        MyAvalonEditor.Document = new TextDocument
        {
            Text = JsonSerializer.Serialize(customVersions ?? _defaultCustomVersions, new JsonSerializerOptions { WriteIndented = true })
        };
    }

    public DictionaryEditor(IDictionary<string, bool> options, string title) : this(title)
    {
        MyAvalonEditor.Document = new TextDocument
        {
            Text = JsonSerializer.Serialize(options ?? _defaultOptions, new JsonSerializerOptions { WriteIndented = true })
        };
    }

    public DictionaryEditor(IDictionary<string, KeyValuePair<string, string>> options, string title) : this(title)
    {
        MyAvalonEditor.Document = new TextDocument
        {
            Text = JsonSerializer.Serialize(options ?? _defaultMapStructTypes, new JsonSerializerOptions { WriteIndented = true })
        };
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (Title)
            {
                case "Versioning Configuration (Custom Versions)":
                    CustomVersions = JsonSerializer.Deserialize<List<FCustomVersion>>(MyAvalonEditor.Document.Text);
                    // DialogResult = !CustomVersions.SequenceEqual(_defaultCustomVersions);
                    DialogResult = true;
                    Close();
                    break;
                case "Versioning Configuration (Options)":
                    Options = JsonSerializer.Deserialize<Dictionary<string, bool>>(MyAvalonEditor.Document.Text);
                    // DialogResult = !Options.SequenceEqual(_defaultOptions);
                    DialogResult = true;
                    Close();
                    break;
                case "Versioning Configuration (MapStructTypes)":
                    MapStructTypes = JsonSerializer.Deserialize<Dictionary<string, KeyValuePair<string, string>>>(MyAvalonEditor.Document.Text);
                    // DialogResult = !Options.SequenceEqual(_defaultOptions);
                    DialogResult = true;
                    Close();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
        catch
        {
            HeBrokeIt.Text = "GG YOU BROKE THE FORMAT, FIX THE JSON OR RESET THE CHANGES!";
            HeBrokeIt.Foreground = new SolidColorBrush((Color) ColorConverter.ConvertFromString(Constants.RED));
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        MyAvalonEditor.Document = Title switch
        {
            "Versioning Configuration (Custom Versions)" => new TextDocument
            {
                Text = JsonSerializer.Serialize(_defaultCustomVersions, new JsonSerializerOptions { WriteIndented = true })
            },
            "Versioning Configuration (Options)" => new TextDocument
            {
                Text = JsonSerializer.Serialize(_defaultOptions, new JsonSerializerOptions { WriteIndented = true })
            },
            "Versioning Configuration (MapStructTypes)" => new TextDocument
            {
                Text = JsonSerializer.Serialize(_defaultMapStructTypes, new JsonSerializerOptions { WriteIndented = true })
            },
            _ => throw new NotImplementedException()
        };
    }
}
