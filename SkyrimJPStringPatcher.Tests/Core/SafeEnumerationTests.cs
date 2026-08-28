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

    /// <summary>Known bug (found while investigating priority bugs after v0.55.2):
    /// onItem itself was called OUTSIDE SafeForEach's try/catch, so an exception
    /// from processing a perfectly-enumerable item (e.g. accessing a lazily-bound
    /// Mutagen field that turns out to be corrupt) was never caught at all — it
    /// propagated straight past SafeForEach, defeating the exact fail-open
    /// protection every PickUpTargetRunner.cs call site relies on it for.
    /// The enumerator itself is fine in this case (MoveNext/Current succeeded),
    /// so unlike an enumerator failure this must NOT abandon the rest of the
    /// sequence -- only the one failing item's processing is lost.</summary>
    [Fact]
    public void SafeForEach_OnItemThrows_ReportsOnError_AndContinuesWithRemainingItems()
    {
        var visited = new List<int>();
        var errors = new List<Exception>();

        SafeEnumeration.SafeForEach(Enumerable.Range(0, 5), i =>
        {
            if (i == 2) throw new InvalidOperationException($"simulated onItem failure at {i}");
            visited.Add(i);
        }, errors.Add);

        // Every item except the failing one was visited -- MoveNext still works
        // fine for a plain List/array-backed enumerator, so processing continues
        // past the failure instead of abandoning items 3 and 4.
        Assert.Equal(new[] { 0, 1, 3, 4 }, visited);
        var error = Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void SafeForEach_OnItemThrowsForMultipleItems_ReportsOnErrorForEach()
    {
        var visited = new List<int>();
        var errors = new List<Exception>();

        SafeEnumeration.SafeForEach(Enumerable.Range(0, 5), i =>
        {
            if (i == 1 || i == 3) throw new InvalidOperationException($"simulated onItem failure at {i}");
            visited.Add(i);
        }, errors.Add);

        Assert.Equal(new[] { 0, 2, 4 }, visited);
        Assert.Equal(2, errors.Count);
    }
}
