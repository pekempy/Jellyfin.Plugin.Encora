using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Encora.Configuration;

/// <summary>
/// The configuration options.
/// </summary>
public enum SomeOptions
{
    /// <summary>
    /// Option one.
    /// </summary>
    OneOption,

    /// <summary>
    /// Second option.
    /// </summary>
    AnotherOption
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        EncoraAPIKey = string.Empty;
        StageMediaAPIKey = string.Empty;
        AddMasterDirector = false;
        TitleFormat = "{show} - {date}";
        DateReplaceChar = "x";
    }

    /// <summary>
    ///  Gets or sets the Encora API Key.
    /// </summary>
    public string EncoraAPIKey { get; set; }

    /// <summary>
    /// Gets or sets the StageMedia API key.
    /// </summary>
    public string StageMediaAPIKey { get; set; }

    /// <summary>
    ///  Gets or sets a value indicating whether the checkbox for add master as director is checked or not.
    /// </summary>
    public bool AddMasterDirector { get; set; }

    /// <summary>
    /// Gets or sets the Title Format.
    /// </summary>
    public string TitleFormat { get; set; }

    /// <summary>
    ///  Gets or sets the date replace character.
    /// </summary>
    public string DateReplaceChar { get; set; }
}
