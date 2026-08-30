namespace SkyrimJPStringPatcher.Tests;

/// <summary>
/// v0.57.1: <c>ModGlossary.DirectoryPath</c> deliberately resolves against
/// <see cref="Directory.GetCurrentDirectory"/>, not <see cref="AppContext.BaseDirectory"/>
/// (see that property's own doc comment) — a person edits those files, so
/// they must live next to where the tool runs, not buried in bin/. That
/// makes the process's current directory another <c>Console.Out</c>-style
/// process-wide shared resource: several tests' <c>SeedModGlossary</c>
/// helpers write into whatever that resolves to.
///
/// Found (audited after fixing the Console.Out flakiness in this same
/// version, in response to being asked to check for other timing-sensitive
/// tests) to currently be harmless only by coincidence — every test that
/// seeds a mod-scoped glossary happens to use the same plugin name with the
/// same seeded value everywhere it's reused, and the suite is serialized
/// (xunit.runner.json) so no concurrent writers exist today. Still fragile:
/// a future test reusing one of those plugin names with a different
/// expectation would silently observe another test's leftover file (which
/// persists on disk across `dotnet test` invocations, not just within one
/// run). This isolates one test's current directory to its own scratch
/// folder for the scope's lifetime, so its ModGlossary reads/writes never
/// touch the shared default location at all.
/// </summary>
internal sealed class CurrentDirectoryScope : IDisposable
{
    private readonly string _original;

    public CurrentDirectoryScope(string directory)
    {
        Directory.CreateDirectory(directory);
        _original = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(directory);
    }

    public void Dispose() => Directory.SetCurrentDirectory(_original);
}
