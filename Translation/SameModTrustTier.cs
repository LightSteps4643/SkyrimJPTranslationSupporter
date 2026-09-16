namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// The trust-tier table issue #4's "c" block (same-mod hints) draws on —
/// which Notes/Method tags are eligible at all, their relative priority (used
/// as a tie-break when two pool entries score identically against the same
/// candidate — same-mod hints from a more trustworthy source should win a
/// tie), and the short English description shown in the block's legend.
///
/// The `SJPTS_AutoCorpus`* family (`SJPTS_AutoCorpus`/`SJPTS_AutoCorpusDsd`/`SJPTS_AutoCorpusImported`/
/// `SJPTS_AutoCorpusReferenceTaiyaku`/`SJPTS_AutoCorpusOverride`) is excluded: those rows
/// are, by construction, already present in the `corpus` that
/// <see cref="PrecedentRetriever"/> searches (see <c>PromptGenerator</c>'s own
/// corpus+imported+reference union), so they can already surface via
/// "Reference examples" — repeating them here would just be a redundant,
/// budget-wasting duplicate. The `*NoJapanese` variants (a response that
/// parsed but contained no Japanese, saved for manual review) are excluded
/// because they may be a genuine translation failure — showing one as an
/// established consistency hint could propagate that failure into every
/// other candidate that happens to share its vocabulary.
/// </summary>
internal static class SameModTrustTier
{
    private static readonly (string Method, int Priority, string Description)[] Tiers =
    {
        ("SJPTS_ModifiedByUser", 1, "manually corrected or confirmed by a human"),
        ("SJPTS_AutoCorpusMeaning", 2, "meaning-translated using this mod's vanilla corpus"),
        ("SJPTS_AutoCorpusMeaningTranslit", 3, "meaning-translated using the vanilla corpus, partly transliterated"),
        ("SJPTS_AutoCorpusTransliterate", 4, "transliterated from a vanilla proper noun"),
        ("SJPTS_AutoCrossModPrecedent", 5, "reused from another mod's identical record"),
        ("SJPTS_TranslationCloudLlm", 6, "translated by a cloud AI (may reflect its own interpretation)"),
        ("SJPTS_TranslationLocalLlm", 7, "translated by a local LLM (may reflect its own interpretation)"),
        ("SJPTS_TranslationNameFallback", 8, "mechanically assembled from known proper nouns (low precision)"),
    };

    public static bool IsEligible(string method) => Tiers.Any(t => t.Method == method);

    /// <summary>Lower is more trustworthy. Throws for an ineligible method —
    /// callers must check <see cref="IsEligible"/> first (mirrors how the
    /// pool is always pre-filtered before this is consulted).</summary>
    public static int PriorityOf(string method) => Tiers.First(t => t.Method == method).Priority;

    public static string DescriptionOf(string method) => Tiers.First(t => t.Method == method).Description;
}
