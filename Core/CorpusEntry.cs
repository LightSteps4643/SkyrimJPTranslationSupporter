namespace SkyrimJPStringPatcher.Core;

/// <summary>One confirmed English→Japanese pair, harvested from the load order itself.
///
/// <paramref name="DsdType"/> records WHICH kind of string this pair came from
/// ("ARMO FULL", "INFO NAM1", ...) — added in v0.5.0 so precedent lookup can
/// prefer examples of the same kind as the candidate being translated. Empty
/// when unknown (e.g. rows read back from a pre-v0.5.0 corpus.tsv).</summary>
public sealed record CorpusEntry(string English, string Japanese, string Source, string SourceKind, string DsdType = "");
