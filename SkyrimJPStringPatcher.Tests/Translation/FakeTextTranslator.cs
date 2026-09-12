using SkyrimJPStringPatcher.Translation;

namespace SkyrimJPStringPatcher.Tests.Translation;

/// <summary>Test double for ITextTranslator (the seam PromptGenerator's ⑤⑥
/// LLM steps are built around) — lets PromptGenerator tests exercise the
/// success/failure paths without any real network call or subprocess.
/// PromptGenerator sends the WHOLE batched prompt as one string and expects
/// a "EnglishText&lt;TAB&gt;Japanese" line per candidate back; this fake
/// ignores the prompt's actual content and just returns a fixed canned
/// response (extra lines for candidates not actually in the batch are
/// harmless — PromptGenerator only looks up the lines it asked about).</summary>
public sealed class FakeTextTranslator : ITextTranslator
{
    private readonly string? _response;
    private readonly string _error;
    public int CallCount { get; private set; }

    /// <summary>The exact prompt text passed to the most recent TryTranslate
    /// call — lets a test assert against what PromptGenerator actually sent
    /// (e.g. to verify the prompt_{localLLM|cloudLLM}_batch*.txt debug files
    /// it writes match byte-for-byte), without re-deriving the expected text.</summary>
    public string? LastPromptReceived { get; private set; }

    private FakeTextTranslator(string? response, string error)
    {
        _response = response;
        _error = error;
    }

    /// <summary>2026-09-12: matches the real prompt's own requirement (see
    /// PromptGenerator.LlmBatchInstruction) that the model echo the source
    /// wrapped in the SAME &lt;SJPTS_TARGET&gt;/&lt;/SJPTS_TARGET&gt; tags it was sent
    /// in, rather than a bare "English&lt;TAB&gt;Japanese" line — this is what a
    /// well-behaved model's answer looks like under the new design. A test
    /// that needs to feed a malformed/raw response (missing tags, extra text
    /// around them, etc. — see <see cref="SucceedingRaw"/>) is exercising a
    /// DIFFERENT scenario than "the model behaved" and should use that
    /// instead, not fight this helper's auto-wrapping.</summary>
    private const string TargetTagOpen = "<SJPTS_TARGET>";
    private const string TargetTagClose = "</SJPTS_TARGET>";

    /// <summary>Always answers every candidate with its own English text wrapped in
    /// &lt;SJPTS_TARGET&gt; tags, then Japanese — i.e. a well-behaved model's answer.</summary>
    public static FakeTextTranslator Succeeding(params (string English, string Japanese)[] answers) =>
        new(string.Join("\n", answers.Select(a => $"{TargetTagOpen}{a.English}{TargetTagClose}\t{a.Japanese}")), "");

    /// <summary>Like <see cref="Succeeding"/>, but the caller supplies the exact
    /// response text verbatim — no automatic tag-wrapping. For tests that need to
    /// simulate a model that DIDN'T behave (missing/malformed tags, stray text
    /// around them, etc.), where <see cref="Succeeding"/>'s own wrapping would
    /// get in the way of the exact malformed shape the test needs to construct.</summary>
    public static FakeTextTranslator SucceedingRaw(string rawResponse) => new(rawResponse, "");

    /// <summary>Always fails, as if the backend were unreachable.</summary>
    public static FakeTextTranslator Failing(string error = "simulated failure") => new(null, error);

    public string? TryTranslate(string promptText, out string error)
    {
        CallCount++;
        LastPromptReceived = promptText;
        error = _error;
        return _response;
    }
}
