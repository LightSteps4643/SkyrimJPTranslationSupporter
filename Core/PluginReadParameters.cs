using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Strings;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// The one place that says how plugins are opened for reading. Localized plugins get their text from the
/// strings tables of the staged folder, Japanese first (the tool looks for text that is already Japanese);
/// the tables and a plugin's own text — the strings with no table — are both decoded by the one rule,
/// <see cref="Utf8ThenWindows1252Encoding"/>, instead of by whatever Mutagen picks for the requested language.
/// </summary>
public static class PluginReadParameters
{
    public static BinaryReadParameters Create(string stringsFolder)
    {
        var pluginText = new Utf8ThenWindows1252Encoding();
        return new BinaryReadParameters
        {
            StringsParam = new StringsReadParameters
            {
                TargetLanguage = Language.Japanese,
                StringsFolderOverride = stringsFolder,
                EncodingProvider = new Utf8ThenWindows1252EncodingProvider(),
                NonLocalizedEncodingOverride = pluginText,
                NonTranslatedEncodingOverride = pluginText,
            },
        };
    }
}
