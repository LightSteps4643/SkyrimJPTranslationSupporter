namespace SkyrimJPStringPatcher.Tests.PickUpTarget;

/// <summary>
/// Console.Out is a single process-wide static resource. xUnit runs different
/// test CLASSES in parallel by default, so two tests that each do their own
/// bare `Console.SetOut(capture); ...; Console.SetOut(original);` can race:
/// one test's restore can land while the other's run is still mid-flight,
/// silently redirecting its output away from that test's own capture buffer
/// (confirmed as the cause of an intermittent failure — the captured text was
/// missing everything PickUpTargetRunner.Run() wrote after another test's
/// Console.SetOut(original) fired concurrently).
///
/// This gates that ONE shared resource behind a lock, isolating each caller's
/// redirect+run+restore as one atomic unit — only tests that actually use
/// Console.Out capture ever wait on each other, not the whole suite.
/// </summary>
internal static class ConsoleCapture
{
    private static readonly object Gate = new();

    public static (T Result, string Stdout) Run<T>(Func<T> action)
    {
        lock (Gate)
        {
            var original = Console.Out;
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var result = action();
                return (result, writer.ToString());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}
