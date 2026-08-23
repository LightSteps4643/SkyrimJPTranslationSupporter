namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// v0.49.1: per-stage enable/disable for 2.意味合成/3.音訳分解/4.NameFallbackTranslator.
/// 1.コーパス完全一致 has no flag here — it's ground-truth data, not inference, so
/// there is no situation where disabling it would be correct (see DESIGN_NOTES.md).
/// All three default to true (unchanged behavior); a user opts OUT of a specific
/// step, not in.
/// </summary>
public sealed record TranslationStageOptions(
    bool EnableMeaning = true,
    bool EnableTransliteration = true,
    bool EnableNameFallback = true)
{
    public static readonly TranslationStageOptions Default = new();
}
