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

    private FakeTextTranslator(string? response, string error)
    {
        _response = response;
        _error = error;
    }

    /// <summary>Always answers every "English&lt;TAB&gt;Japanese" pair given, regardless of what was actually asked.</summary>
    public static FakeTextTranslator Succeeding(params (string English, string Japanese)[] answers) =>
        new(string.Join("\n", answers.Select(a => $"{a.English}\t{a.Japanese}")), "");

    /// <summary>Always fails, as if the backend were unreachable.</summary>
    public static FakeTextTranslator Failing(string error = "simulated failure") => new(null, error);

    public string? TryTranslate(string promptText, out string error)
    {
        CallCount++;
        error = _error;
        return _response;
    }
}
