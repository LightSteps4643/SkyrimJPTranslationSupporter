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
}
