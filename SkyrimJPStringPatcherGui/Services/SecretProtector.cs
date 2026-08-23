using System.Security.Cryptography;
using System.Text;

namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// v0.52.1a: DPAPI (Windows Data Protection API) wrapper for the one secret this
/// GUI persists — a cloud AI API key for step 5's OpenAI-compatible endpoint (see
/// LocalLlmOptions.ApiKey on the CLI side). The rest of gui_settings.json is
/// deliberately plain JSON (see AppSettings' own remarks on that preference), but
/// an API key is a real credential with a billing/abuse blast radius if it leaks
/// via an accidentally-shared project folder, a cloud-synced folder, or a future
/// git commit — DPAPI ties the stored bytes to the current Windows user account,
/// so the file alone is useless to anyone who isn't logged in as that user.
///
/// Not a defense against another process running under the SAME Windows account
/// (DPAPI doesn't gate that) — the threat model here is accidental file-level
/// exposure, not another process on the same session.
/// </summary>
public static class SecretProtector
{
    // Ties the encrypted blob to this specific use — decrypting it for anything
    // else (or a blob DPAPI-encrypted elsewhere) fails outright rather than
    // silently succeeding with garbage.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SkyrimJPStringPatcherGui.LlmApiKey.v1");

    /// <summary>Plaintext -&gt; DPAPI-encrypted, Base64-encoded (JSON-safe) string.
    /// Empty input returns empty output (no point encrypting "no key set").</summary>
    public static string Protect(string plaintext)
    {
        if (plaintext.Length == 0) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Reverses <see cref="Protect"/>. Returns "" for empty input, and
    /// also for a blob that fails to decrypt (corrupted file, or encrypted under a
    /// different Windows account/machine — DPAPI keys are not portable) rather
    /// than throwing and blocking the whole settings load.</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (protectedBase64.Length == 0) return "";
        try
        {
            var encrypted = Convert.FromBase64String(protectedBase64);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
