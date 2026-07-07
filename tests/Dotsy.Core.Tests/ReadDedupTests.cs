using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ReadDedupTests
{
    private const string Content = "  1  public class Foo\n  2  {\n  3  }";
    private const string Path = @"C:\proj\Foo.cs";

    private static Dictionary<string, ReadCacheEntry> Cache(
        long mtime = 100, long size = 40, int offset = 0, int limit = 2000, string content = Content) =>
        new() { [Path] = new ReadCacheEntry(mtime, size, offset, limit, content) };

    private static List<Message> MessagesWith(string content) =>
        [new UserMessage([new ToolResultBlock("t1", content, false)])];

    [TestMethod]
    public void RepeatRead_LiveUnchangedSameRange_ReturnsStub()
    {
        var stub = ReadDedup.StubForRepeatRead(
            Cache(), MessagesWith(Content), Path, mtimeTicks: 100, size: 40, offset: 0, limit: 2000);

        Assert.IsNotNull(stub);
        StringAssert.Contains(stub, "already read");
    }

    [TestMethod]
    public void FirstRead_CacheMiss_ReturnsNull()
    {
        var stub = ReadDedup.StubForRepeatRead(
            new Dictionary<string, ReadCacheEntry>(), MessagesWith(Content), Path, 100, 40, 0, 2000);
        Assert.IsNull(stub);
    }

    [TestMethod]
    public void FileChangedOnDisk_ReturnsNull()
    {
        // Different mtime and size both independently defeat the cache.
        Assert.IsNull(ReadDedup.StubForRepeatRead(Cache(), MessagesWith(Content), Path, mtimeTicks: 999, size: 40, offset: 0, limit: 2000));
        Assert.IsNull(ReadDedup.StubForRepeatRead(Cache(), MessagesWith(Content), Path, mtimeTicks: 100, size: 999, offset: 0, limit: 2000));
    }

    [TestMethod]
    public void RangeExtendingBeyondCached_ReturnsNull()
    {
        // Requested lines reach past what was read -> the extra lines aren't in context, so re-read.
        Assert.IsNull(ReadDedup.StubForRepeatRead(Cache(), MessagesWith(Content), Path, 100, 40, offset: 50, limit: 2000));   // ends at 2050 > 2000
        Assert.IsNull(ReadDedup.StubForRepeatRead(Cache(offset: 100, limit: 50), MessagesWith(Content), Path, 100, 40, offset: 80, limit: 20)); // starts before 100
    }

    [TestMethod]
    public void ContainedRange_ReturnsStub()
    {
        // A sub-range of an already-read (still-live) span is present in that output -> skip the re-read.
        // This is the common cascade: read a file whole, then re-read overlapping slices of it.
        Assert.IsNotNull(ReadDedup.StubForRepeatRead(Cache(), MessagesWith(Content), Path, 100, 40, offset: 0, limit: 20));
        Assert.IsNotNull(ReadDedup.StubForRepeatRead(Cache(), MessagesWith(Content), Path, 100, 40, offset: 500, limit: 100));
        // Exact-range re-read is still covered.
        Assert.IsNotNull(ReadDedup.StubForRepeatRead(Cache(), MessagesWith(Content), Path, 100, 40, offset: 0, limit: 2000));
    }

    [TestMethod]
    public void ContentEvictedByCompaction_ReturnsNull()
    {
        // The key compaction-safety case: cache still has the entry, but the content is no longer in
        // the live messages (summarized away), so the full file must be returned again.
        var summarized = new List<Message> { new UserMessage([new TextBlock("[tool summary] Read returned: public class Foo...")]) };
        Assert.IsNull(ReadDedup.StubForRepeatRead(Cache(), summarized, Path, 100, 40, 0, 2000));

        // No messages at all -> also null.
        Assert.IsNull(ReadDedup.StubForRepeatRead(Cache(), new List<Message>(), Path, 100, 40, 0, 2000));
    }

    [TestMethod]
    public void ContentStillLive_MatchesExactToolResultOnly()
    {
        Assert.IsTrue(ReadDedup.ContentStillLive(MessagesWith(Content), Content));
        Assert.IsFalse(ReadDedup.ContentStillLive(MessagesWith("different"), Content));
        // A summary text block that merely mentions the file is not a verbatim match.
        Assert.IsFalse(ReadDedup.ContentStillLive(
            [new UserMessage([new TextBlock("[tool summary] Read returned: " + Content)])], Content));
    }
}
