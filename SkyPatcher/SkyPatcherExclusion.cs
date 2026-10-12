using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.SkyPatcher;

/// <summary>Which SkyPatcher name strings are not offered for translation. SkyPatcher renames exist
/// to be displayed, so the plugin side's "looks like internal data" exclusions do NOT apply here
/// (they would leave visible names in English). Only a string that cannot be translated, or would
/// break if translated, is left out; the returned code says why (null = offer it).</summary>
public static class SkyPatcherExclusion
{
    public const string AlreadyJapanese = "AlreadyJapanese";
    public const string NoLetters = "NoLetters";
    public const string MarkupOnly = "MarkupOnly";
    public const string InterfaceKeyReference = "InterfaceKeyReference";
    public const string AssetPath = "AssetPath";
    public const string PlaceholderToken = "PlaceholderToken";
    public const string NonWordAcronym = "NonWordAcronym";

    public static string? Reason(string? text)
    {
        if (LanguageDetector.ContainsJapanese(text)) return AlreadyJapanese;
        if (!LanguageDetector.IsTranslatableEnglish(text)) return NoLetters;
        if (NonTranslatableText.IsMarkupOnly(text)) return MarkupOnly;
        if (NonTranslatableText.LooksLikeInterfaceTranslationKeyReference(text)) return InterfaceKeyReference;
        if (NonTranslatableText.IsAssetPath(text)) return AssetPath;
        if (NonTranslatableText.IsPlaceholderToken(text)) return PlaceholderToken;
        if (NonTranslatableText.LooksLikeNonWordAcronym(text)) return NonWordAcronym;
        return null;
    }
}
