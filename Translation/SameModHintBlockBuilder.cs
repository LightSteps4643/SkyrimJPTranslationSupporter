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

    /// <summary>Method tag for a human's own edit via the GUI — the single
    /// highest trust tier (<see cref="SameModTrustTier"/> priority 1), split
    /// out into its own imperative sub-block (see <see cref="BuildBlock"/>'s
    /// remarks) instead of being listed alongside every other tier.</summary>
    private const string ConfirmedByHumanMethod = "SJPTS_ModifiedByUser";

    /// <summary>Builds issue #4's "c" block, or "" when there is nothing to
    /// show — no relevant hint exists, or none fits even at a single line (no
    /// floor, exactly like "Reference examples" having none: an empty section
    /// is a valid, unremarkable outcome, not truncated or forced in over
    /// budget).
    ///
    /// 2026-09-16: the (non-human) tiers use a legend + numeric code per line
    /// (only for trust tiers actually present among the selected hints)
    /// instead of writing out each hint's source in full — real large-scale
    /// LLM testing (104 candidates across 30 ambiguous words) confirmed this
    /// compressed form works (8 ESP improvements, 0 false positives) and
    /// costs far less of the budget than repeating a description on every
    /// line.
    ///
    /// 2026-09-18 real-data finding (Light Greatswords.esp): a human had
    /// filled in mod_glossary.tsv with "Light Greatsword" -&gt; "軽大剣", but
    /// every "X Light Greatsword" candidate was still translated word-for-word
    /// ("Light"="光") — the softer "keep wording consistent, defer to
    /// Reference examples if conflict" framing (below) is not strong enough
    /// to override the model's own dictionary instinct, especially against a
    /// competing "Known translations for words: ...Light=光..." hint on the
    /// SAME candidate. <see cref="ConfirmedByHumanMethod"/> hints are now
    /// split into their own separate, imperative "IMPORTANT" sub-block that
    /// explicitly outranks even "Reference examples" — verified via a real
    /// local-LLM (gemma4:26b, reasoning off) A/B test on the actual failing
    /// prompt before this was implemented (see management repo's design
    /// docs). Every other trust tier keeps the original softer wording
    /// unchanged — the deference to "Reference examples" remains correct for
    /// an ordinary (non-human-confirmed) same-mod hint, which really is just
    /// this tool's own earlier guess.</summary>
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

        var budget = (int)(batchCharLimit * BudgetRatio);
        var confirmedHints = hints.Where(h => h.SourceKind == ConfirmedByHumanMethod).ToList();
        var otherHints = hints.Where(h => h.SourceKind != ConfirmedByHumanMethod).ToList();

        var result = new System.Text.StringBuilder();
        var remainingBudget = budget;

        if (confirmedHints.Count > 0)
        {
            const string confirmedHeader =
                "IMPORTANT — the following term(s) have been explicitly confirmed by a human as the correct\n" +
                "translation for this mod. If a candidate below contains this exact phrase (or is this exact\n" +
                "phrase plus other words, e.g. a material prefix), you MUST use this exact Japanese term for that\n" +
                "part, overriding your own judgment, any \"Known translations for words\" entries for the\n" +
                "individual words inside it, and even \"Reference examples\":\n";

            var confirmedBody = new System.Text.StringBuilder();
            foreach (var hint in confirmedHints)
            {
                var line = $"    \"{hint.English}\" -> \"{hint.Japanese}\"\n";
                if (confirmedHeader.Length + confirmedBody.Length + line.Length > remainingBudget) break;
                confirmedBody.Append(line);
            }
            if (confirmedBody.Length > 0)
            {
                result.Append(confirmedHeader).Append(confirmedBody).Append('\n');
                remainingBudget -= confirmedHeader.Length + confirmedBody.Length + 1;
            }
        }

        if (otherHints.Count > 0 && remainingBudget > 0)
        {
            const string headerIntro =
                "Same-mod translations so far (translations already established earlier in this same session, for this\n" +
                "same mod — use them to keep your own wording consistent, but defer to \"Reference examples\" if the two\n" +
                "conflict; the number after each pair below indicates how it was produced, per this legend):\n";

            var usedMethods = otherHints.Select(h => h.SourceKind).Distinct().OrderBy(SameModTrustTier.PriorityOf).ToList();
            var legend = string.Concat(usedMethods.Select(m => $"  {SameModTrustTier.PriorityOf(m)}={SameModTrustTier.DescriptionOf(m)}\n"));
            var header = headerIntro + legend;

            var body = new System.Text.StringBuilder();
            foreach (var hint in otherHints)
            {
                var line = $"    \"{hint.English}\"\t\"{hint.Japanese}\"\t{SameModTrustTier.PriorityOf(hint.SourceKind)}\n";
                if (header.Length + body.Length + line.Length > remainingBudget) break;
                body.Append(line);
            }
            if (body.Length > 0)
                result.Append(header).Append(body).Append('\n');
        }

        return result.ToString();
    }
}
