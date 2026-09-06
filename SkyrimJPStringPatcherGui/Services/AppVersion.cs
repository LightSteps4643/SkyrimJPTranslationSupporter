namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// Reads this assembly's own version (set via MSBuild's Version property,
/// itself derived from the git tag at publish time by publish-release.ps1 —
/// see that script's own remarks) for display in the window title. A plain
/// `dotnet build`/`dotnet run` never sets that property, so it falls back to
/// the .NET SDK's implicit default (1.0.0) — harmless, since dev builds
/// aren't distributed anyway.
/// </summary>
public static class AppVersion
{
    public static string FormatWindowTitle(string baseName, Version? assemblyVersion) =>
        assemblyVersion is null ? baseName : $"{baseName} v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
}
