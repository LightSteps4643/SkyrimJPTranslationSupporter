using Mutagen.Bethesda;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace SkyrimJPStringPatcher.Core;

/// <summary>
/// The encoding rule for the strings tables (.STRINGS / .DLSTRINGS / .ILSTRINGS), the same for every language:
/// each string as UTF-8, else Windows-1252 (<see cref="Utf8ThenWindows1252Encoding"/>). Mutagen picks per language
/// — English: Windows-1252 only — but Skyrim SE's own English tables carry non-ASCII text as UTF-8 (the vanilla
/// "Daedric Plate" message title holds a no-break space, C2 A0), which Mutagen's English choice reads as "Â ".
/// The tool only ever asks for the English and Japanese tables (Japanese tables are UTF-8); a language whose legacy
/// tables used another code page (Russian, Polish, Chinese …) is not read here.
/// </summary>
public sealed class Utf8ThenWindows1252EncodingProvider : IMutagenEncodingProvider
{
    private static readonly Utf8ThenWindows1252Encoding Rule = new();

    public IMutagenEncoding GetEncoding(GameRelease release, Language language) => Rule;
}
