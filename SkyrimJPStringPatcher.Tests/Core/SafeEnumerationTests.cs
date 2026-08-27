using SkyrimJPStringPatcher.Core;

namespace SkyrimJPStringPatcher.Tests.Core;

public class SafeEnumerationTests
{
    /// <summary>An IEnumerable whose enumerator throws once it reaches
    /// <paramref name="failAt"/> — simulates Mutagen's lazy binary overlay
    /// throwing mid-iteration on a malformed record (DESIGN_NOTES.md known
    /// issue 21), which a plain `foreach` cannot recover from.</summary>
    private static IEnumerable<int> SequenceThatThrowsAt(int failAt, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (i == failAt) throw new InvalidOperationException($"simulated failure at {i}");
            yield return i;
        }
    }

    [Fact]
    public void SafeForEach_NormalSequence_VisitsEveryItem_NeverCallsOnError()
    {
        var visited = new List<int>();
        var errors = new List<Exception>();

        SafeEnumeration.SafeForEach(Enumerable.Range(0, 5), visited.Add, errors.Add);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, visited);
        Assert.Empty(errors);
    }

    [Fact]
    public void SafeForEach_EmptySequence_NeverCallsOnItemOrOnError()
    {
        var visited = new List<int>();
        var errors = new List<Exception>();

        SafeEnumeration.SafeForEach(Enumerable.Empty<int>(), visited.Add, errors.Add);

        Assert.Empty(visited);
        Assert.Empty(errors);
    }

    [Fact]
    public void SafeForEach_ThrowingEnumerator_KeepsItemsBeforeTheFailure_ReportsOnError_StopsIterating()
    {
        var visited = new List<int>();
        var errors = new List<Exception>();

        // 5 items total, throws when it would produce the 3rd (index 3).
        SafeEnumeration.SafeForEach(SequenceThatThrowsAt(failAt: 3, count: 5), visited.Add, errors.Add);

        // Items 0,1,2 (produced before the throw) were still visited — the whole
        // sequence is not lost, only the tail after the failure point.
        Assert.Equal(new[] { 0, 1, 2 }, visited);
        var error = Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void SafeForEach_ThrowsImmediately_ReportsOnErrorWithNoItemsVisited()
    {
        var visited = new List<int>();
        var errors = new List<Exception>();

        SafeEnumeration.SafeForEach(SequenceThatThrowsAt(failAt: 0, count: 3), visited.Add, errors.Add);

        Assert.Empty(visited);
        Assert.Single(errors);
    }
}
