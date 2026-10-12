namespace SkyrimJPStringPatcherGui.Services;

/// <summary>Runs steps in order and stops at the first one that fails. The common buttons chain the plugin run
/// and the SkyPatcher run this way: the SkyPatcher run reads the plugin side's finished translations as
/// hints, so it must not go on after a failed plugin run.</summary>
public static class StepRunner
{
    /// <returns>true when every step succeeded (also when there are none).</returns>
    public static async Task<bool> RunUntilFailureAsync(IEnumerable<Func<Task<bool>>> steps)
    {
        foreach (var step in steps)
            if (!await step()) return false;
        return true;
    }
}
