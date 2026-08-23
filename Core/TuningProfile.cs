namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// How hard the mining steps should work, traded against how long the run takes.
///
/// The distinction exists because the two situations this tool runs in have
/// opposite priorities. While DEVELOPING it, the same stage gets re-run dozens of
/// times and a fast turnaround is what makes iteration possible. But the tool a
/// user installs is run ONCE per modlist build — it is not something anyone sits
/// and waits on repeatedly — so there a slower, wider search is simply better.
/// The defaults were originally chosen for the first situation and quietly
/// applied to both, which meant the shipped behaviour was tuned for a constraint
/// that does not exist for the person running it.
///
/// <see cref="Thorough"/> widens the RECALL knobs (how much text is examined, how
/// weak a statistical signal is still worth considering) but deliberately leaves
/// the PRECISION checks alone — the corpus-witness verification in
/// <c>SentenceAlignmentMiner</c>, the "official vs derived" rule, the
/// corroboration counts. Casting a wider net is safe precisely because those
/// checks still decide what is kept, so the extra time buys candidates to verify
/// rather than unverified guesses.
/// </summary>
public sealed record TuningProfile(
    int SentenceMinCooccurrence,
    double SentenceMinDice,
    int SentenceMaxEnglishLength,
    int SentenceMaxWordsPerEntry,
    int SentenceMaxRunsPerEntry,
    int TransliteratorMaxIterations)
{
    /// <summary>Bounded search. Kept as an escape hatch, but no longer the default
    /// — measurement showed the wider search costs only about three extra seconds
    /// on a 94k-entry corpus, which is not worth trading accuracy for even while
    /// developing (v0.16.0).</summary>
    public static readonly TuningProfile Fast = new(
        SentenceMinCooccurrence: 3,
        SentenceMinDice: 0.8,
        SentenceMaxEnglishLength: 300,
        SentenceMaxWordsPerEntry: 40,
        SentenceMaxRunsPerEntry: 20,
        TransliteratorMaxIterations: 8);

    /// <summary>The default: examine everything, consider weaker signals, and let
    /// the bootstrap run to a fixed point. Verification is unchanged.</summary>
    public static readonly TuningProfile Thorough = new(
        SentenceMinCooccurrence: 2,
        SentenceMinDice: 0.45,
        SentenceMaxEnglishLength: 20000,
        SentenceMaxWordsPerEntry: 3000,
        SentenceMaxRunsPerEntry: 1000,
        TransliteratorMaxIterations: 100);

    public static TuningProfile Current { get; private set; } = Thorough;

    public static void Use(TuningProfile profile) => Current = profile;

    public string Name => ReferenceEquals(this, Thorough) ? "thorough" : "fast";
}
