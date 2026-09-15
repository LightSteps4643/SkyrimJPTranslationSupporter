using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Formats issue #4's "c" block (see <see cref="SameModHintFinder"/> for the
/// relevance logic this wraps) into actual prompt text, and decides which
/// already-resolved candidates are trustworthy enough to draw hints from in
/// the first place.
/// </summary>
public static class SameModHintBlockBuilder
{
    // 2026-09-16: how much of the batch's own char limit this shared,
    // once-per-batch block may use — same 25% share as "b" (Reference
    // examples), since both compete for the same overall prompt budget.
    private const double BudgetRatio = 0.25;

    /// <summary>The trust-tier Notes/Method tags this hint is allowed to draw
    /// on. The `AutoCorpus`* family (`AutoCorpus`/`AutoCorpusDsd`/
    /// `AutoCorpusImported`/`AutoCorpusReferenceTaiyaku`/`AutoCorpusOverride`)
    /// is excluded: those rows are, by construction, already present in the
    /// `corpus` that <see cref="PrecedentRetriever"/> searches (see
    /// <c>PromptGenerator</c>'s own corpus+imported+reference union), so they
    /// can already surface via "Reference examples" — repeating them here
    /// would just be a redundant, budget-wasting duplicate. The
    /// `*NoJapanese` variants (a response that parsed but contained no
    /// Japanese, saved for manual review) are excluded because they may be a
    /// genuine translation failure — showing one as an established
    /// consistency hint could propagate that failure into every other
    /// candidate that happens to share its vocabulary.
    ///
    /// "ModifiedByUser" (a human's own confirmed correction) is the highest-
    /// trust tier in the original design but has no producer in this same-run
    /// pipeline today — it only exists once a saved translations.tsv with a
    /// prior manual edit is loaded back in a LATER run, which is out of scope
    /// for this same-run consistency fix (issue #4's other reported half,
    /// cross-run consistency, is intentionally deferred).</summary>
    private static readonly HashSet<string> EligibleMethods = new(StringComparer.Ordinal)
    {
        "AutoCorpusMeaning",
        "AutoCorpusMeaningTranslit",
        "AutoCorpusTransliterate",
        "AutoCrossModPrecedent",
        "TranslationCloudLlm",
        "TranslationLocalLlm",
        "TranslationNameFallback",
    };

    public static bool IsEligibleMethod(string method) => EligibleMethods.Contains(method);

    /// <summary>Builds the "Same-mod translations so far" block, or "" when
    /// there is nothing to show — no relevant hint exists, or none fits even
    /// at a single line (no floor, exactly like "Reference examples" having
    /// none: an empty section is a valid, unremarkable outcome, not
    /// truncated or forced in over budget).</summary>
    /// <param name="pool">This session's own already-resolved translations for
    /// this same mod, already restricted by the caller to <see
    /// cref="IsEligibleMethod"/>.</param>
    /// <param name="candidates">The batch's still-unresolved candidates (English
    /// text and DSD record type) to find hints relevant to ANY of them.</param>
    /// <param name="batchCharLimit">The same limit the batch itself is packed
    /// against — this block may use up to <see cref="BudgetRatio"/> of it.</param>
    public static string BuildBlock(IReadOnlyList<CorpusEntry> pool, IReadOnlyList<(string Text, string RecordType)> candidates, int batchCharLimit)
    {
        if (pool.Count == 0 || candidates.Count == 0) return "";

        var hints = new SameModHintFinder(pool).FindHints(candidates);
        if (hints.Count == 0) return "";

        const string header =
            "Same-mod translations so far:\n" +
            "  \"Same-mod translations so far\" are translations produced by an AI earlier in this same\n" +
            "  session, for this same mod — not yet human-reviewed, so treat them as lower-confidence than\n" +
            "  \"Reference examples\". Use them mainly to keep your own wording consistent with them, but\n" +
            "  defer to \"Reference examples\" if the two conflict.\n";

        var budget = (int)(batchCharLimit * BudgetRatio);
        var body = new System.Text.StringBuilder();
        foreach (var hint in hints)
        {
            var line = $"    \"{hint.English}\" → \"{hint.Japanese}\"\n";
            if (header.Length + body.Length + line.Length > budget) break;
            body.Append(line);
        }

        return body.Length == 0 ? "" : header + body + "\n";
    }
}
