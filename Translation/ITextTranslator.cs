namespace SkyrimJPStringPatcher.Translation;

/// <summary>
/// v0.52.1a: the shape step 5 needs regardless of WHICH backend answers it —
/// an HTTP call to an OpenAI-compatible endpoint (<see cref="LocalLlmTranslator"/>,
/// local Ollama or an authenticated cloud API) or a Claude Code CLI subprocess
/// (<see cref="ClaudeCodeTranslator"/>). PromptGenerator only ever talks to this
/// interface, so adding a third backend later means one new class, not touching
/// PromptGenerator's call sites at all.
/// </summary>
public interface ITextTranslator
{
    /// <summary>Same contract for every implementation: null means "this
    /// candidate stays unresolved, fall through to prompt.txt" — never an
    /// exception for an ordinary failure (unreachable server, non-zero exit,
    /// non-Japanese response, etc.).</summary>
    string? TryTranslate(string promptText, out string error);

    /// <summary>v0.58.4: whether persistent/systemic failures (server
    /// unreachable, timeout, non-2xx status — as opposed to a single
    /// candidate's response just being unparsable/empty/non-Japanese) have hit
    /// the implementation's own consecutive-failure threshold, so the caller
    /// (<see cref="PromptGenerator.ApplyLlmStep"/>) should stop sending it any
    /// more batches for this run. Defaults to false — an implementation with no
    /// circuit-breaker concept of its own (e.g. a test fake) never trips it.
    /// See <see cref="ClaudeCodeTranslator"/>/<see cref="LocalLlmTranslator"/>
    /// for the hard-failure-vs-response-processing-failure distinction that
    /// feeds this.</summary>
    bool CircuitOpen => false;
}
