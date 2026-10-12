using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.PickUpTarget;

/// <summary>
/// The name ("FULL") of a record, offered under its DSD type ("&lt;SIG&gt; FULL") —
/// DSD's kFullName family (38 record kinds) plus "DIAL FULL".
///
/// Most record kinds expose their name through Mutagen's generic
/// <see cref="ITranslatedNamedGetter"/> aspect, which is what the generic path
/// below uses. A few kinds have a translated Name in Mutagen's own record
/// definition but are NOT given that aspect, so the generic path never sees
/// them: <c>Key</c> (KEYM), <c>Class</c> (CLAS) and <c>Flora</c> (FLOR) —
/// found 2026-10-11 by checking every one of the 39 supported record kinds
/// (the plain classes AND their binary-overlay classes) against the aspect;
/// these three were silently never picked up before. They are read explicitly
/// here. Anything else DSD supports but this tool cannot read the name of is
/// reported by <see cref="IsSupportedButUnhandled(IMajorRecordGetter, IReadOnlySet{string}, out string)"/>
/// instead of being dropped without a trace.
///
/// Only record kinds DSD actually has a FULL type for are offered
/// (<see cref="RecordSignatureMap.DsdFullNameSupported"/>): a name DSD cannot
/// apply would be translated for nothing.
/// </summary>
public static class FullNameFields
{
    /// <summary>The record's name as (DSD type, field), or nothing when the record kind is not one DSD
    /// supports a FULL type for, or the record has no name.</summary>
    public static IEnumerable<(string DsdType, ITranslatedStringGetter Field)> For(IMajorRecordGetter record)
    {
        switch (record)
        {
            case IKeyGetter key:
                if (key.Name != null) yield return ("KEYM FULL", key.Name);
                yield break;
            case IClassGetter clas:
                if (clas.Name != null) yield return ("CLAS FULL", clas.Name);
                yield break;
            case IFloraGetter flora:
                if (flora.Name != null) yield return ("FLOR FULL", flora.Name);
                yield break;
        }

        if (record is ITranslatedNamedGetter named && named.Name != null)
        {
            var signature = RecordSignatureMap.Resolve(record.GetType().Name);
            if (RecordSignatureMap.DsdFullNameSupported.Contains(signature))
                yield return ($"{signature} FULL", named.Name);
        }
    }

    private static bool CanReadNameOf(IMajorRecordGetter record) =>
        record is ITranslatedNamedGetter || record is IKeyGetter || record is IClassGetter || record is IFloraGetter;

    // CLR type -> record signature, or "" when this table does not know the type. Looked up once per type, not per record.
    private static readonly ConcurrentDictionary<Type, string> SignatureByType = new();

    /// <summary>True when the record is of a kind whose signature is in <paramref name="supportedSignatures"/>
    /// (DSD supports a FULL type for it) yet this tool has no way to read its name — i.e. such records are
    /// being skipped silently. <paramref name="signature"/> is set then.</summary>
    public static bool IsSupportedButUnhandled(IMajorRecordGetter record, IReadOnlySet<string> supportedSignatures, out string signature)
    {
        signature = "";
        if (CanReadNameOf(record)) return false;

        var known = SignatureByType.GetOrAdd(record.GetType(),
            t => RecordSignatureMap.TryResolve(t.Name, out var s) ? s : "");
        if (known.Length == 0 || !supportedSignatures.Contains(known)) return false;

        signature = known;
        return true;
    }

    /// <summary><see cref="IsSupportedButUnhandled(IMajorRecordGetter, IReadOnlySet{string}, out string)"/> against
    /// the record kinds DSD supports a FULL type for.</summary>
    public static bool IsSupportedButUnhandled(IMajorRecordGetter record, out string signature) =>
        IsSupportedButUnhandled(record, RecordSignatureMap.DsdFullNameSupported, out signature);
}
