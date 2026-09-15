using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// Formats issue #4's "c" block (see <see cref="SameModHintFinder"/> for the
/// relevance logic this wraps) into actual prompt text, using
/// <see cref="SameModTrustTier"/> to decide which already-resolved candidates
/// are trustworthy enough to draw hints from and how to describe them.
/// </summary>
public static class SameModHintBlockBuilder
{
    // 2026-09-16: how much of the batch's own char limit this shared,
    // once-per-batch block may use — same 25% share as "b" (Reference
    // examples), since both compete for the same overall prompt budget.
    private const double BudgetRatio = 0.25;

    public static bool IsEligibleMethod(string method) => SameModTrustTier.IsEligible(method);

    /// <summary>Builds the "Same-mod translations so far" block, or "" when
    /// there is nothing to show — no relevant hint exists, or none fits even
    /// at a single line (no floor, exactly like "Reference examples" having
    /// none: an empty section is a valid, unremarkable outcome, not
    /// truncated or forced in over budget).
    ///
    /// 2026-09-16: uses a legend + numeric code per line (only for trust
    /// tiers actually present among the selected hints) instead of writing
    /// out each hint's source in full — real large-scale LLM testing (104
    /// candidates across 30 ambiguous words) confirmed this compressed form
    /// works (8 ESP improvements, 0 false positives) and costs far less of
    /// the budget than repeating a description on every line.</summary>
    /// <param name="pool">This session's own already-resolved translations for
    /// this same mod, already restricted by the caller to <see
    /// cref="IsEligibleMethod"/> (<see cref="CorpusEntry.SourceKind"/> holds
    /// the Notes/Method tag).</param>
    /// <param name="candidates">The batch's still-unresolved candidates (English
    /// text and DSD record type) to find hints relevant to ANY of them.</param>
    /// <param name="batchCharLimit">The same limit the batch itself is packed
    /// against — this block may use up to <see cref="BudgetRatio"/> of it.</param>
    /// <param name="alreadyShown">English texts already shown verbatim in some
    /// candidate's own "Reference examples" list this batch — skipped here to
    /// avoid a duplicate hint. Pass an empty set (the default) when the
    /// caller has no such list (e.g. Interface翻訳, which has no "b" at all).</param>
    public static string BuildBlock(
        IReadOnlyList<CorpusEntry> pool, IReadOnlyList<(string Text, string RecordType)> candidates, int batchCharLimit,
        IReadOnlySet<string>? alreadyShown = null)
    {
        if (pool.Count == 0 || candidates.Count == 0) return "";

        var hints = new SameModHintFinder(pool).FindHints(candidates);
        if (alreadyShown is { Count: > 0 })
            hints = hints.Where(h => !alreadyShown.Contains(h.English)).ToList();
        if (hints.Count == 0) return "";

        const string headerIntro =
            "Same-mod translations so far (translations already established earlier in this same session, for this\n" +
            "same mod — use them to keep your own wording consistent, but defer to \"Reference examples\" if the two\n" +
            "conflict; the number after each pair below indicates how it was produced, per this legend):\n";

        var usedMethods = hints.Select(h => h.SourceKind).Distinct().OrderBy(SameModTrustTier.PriorityOf).ToList();
        var legend = string.Concat(usedMethods.Select(m => $"  {SameModTrustTier.PriorityOf(m)}={SameModTrustTier.DescriptionOf(m)}\n"));
        var header = headerIntro + legend;

        var budget = (int)(batchCharLimit * BudgetRatio);
        var body = new System.Text.StringBuilder();
        foreach (var hint in hints)
        {
            var line = $"    \"{hint.English}\"\t\"{hint.Japanese}\"\t{SameModTrustTier.PriorityOf(hint.SourceKind)}\n";
            if (header.Length + body.Length + line.Length > budget) break;
            body.Append(line);
        }

        return body.Length == 0 ? "" : header + body + "\n";
    }
}
