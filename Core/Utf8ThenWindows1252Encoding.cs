using System.Text;
using System.Text.Unicode;
using Mutagen.Bethesda.Strings;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// How the bytes of a plugin's own (non-localized) text become a string: the bytes of each string are read as
/// UTF-8, and when they are not valid UTF-8 they are read as Windows-1252 — what Skyrim and the Creation Kit do,
/// string by string (the Creation Kit writes Windows-1252, other editors write UTF-8, and one plugin can hold both).
/// The bytes carry no encoding label, so a rule is needed; Mutagen's own choice depends on the language the
/// strings are requested in (English: Windows-1252 only; any other language, Japanese included: UTF-8 only, with
/// invalid bytes replaced by U+FFFD), which turned a Windows-1252 "’" (0x92) into U+FFFD — and that cannot be
/// repaired after the fact.
/// Deliberately not a Shift-JIS lane: Shift-JIS and Windows-1252 cannot be told apart reliably (English text with
/// "’" before a letter is a valid Shift-JIS pair), and Japanese text in plugins is UTF-8. A Windows-1252 text that
/// happens to be valid UTF-8 is read as UTF-8, as the game does. Mutagen's own "UTF-8 then 1252" presets are not
/// used: their UTF-8 half replaces invalid bytes instead of failing, so the fallback never runs.
/// Read-only: this tool never writes plugins.
/// </summary>
public sealed class Utf8ThenWindows1252Encoding : IMutagenEncoding
{
    private static readonly Encoding Windows1252 = CreateWindows1252();

    private static Encoding CreateWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    public string GetString(ReadOnlySpan<byte> bytes) =>
        Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Windows1252.GetString(bytes);

    public int GetByteCount(ReadOnlySpan<char> chars) => throw ReadOnly();

    public int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes) => throw ReadOnly();

    private static NotSupportedException ReadOnly() =>
        new("This encoding only reads plugin text; the tool does not write plugins.");
}
