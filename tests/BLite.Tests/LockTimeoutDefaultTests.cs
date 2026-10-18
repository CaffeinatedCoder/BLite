using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using BLite.Core;
using BLite.Core.Storage;
using BLite.Core.Transactions;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// #303: a writer waits for the commits queued ahead of it, and each commit waits for the disk, so
/// <see cref="LockTimeout.Default"/> must outlast several slow commits, and a timeout set on
/// <see cref="PageFileConfig.LockTimeout"/> must reach every lock that waits with it.
/// </summary>
public class LockTimeoutDefaultTests : IDisposable
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"locktimeout_{Guid.NewGuid():N}");

    public LockTimeoutDefaultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Default_IsFiveSecondsForReadsAndWrites()
    {
        Assert.Equal(LockTimeout.From(readTimeoutMs: 5_000, writeTimeoutMs: 5_000, maxConcurrentWriters: 96), LockTimeout.Default);
    }

    [Fact]
    public void Presets_AndDetectedConfigs_UseTheDefault()
    {
        Assert.Equal(LockTimeout.Default, PageFileConfig.Small.LockTimeout);
        Assert.Equal(LockTimeout.Default, PageFileConfig.Default.LockTimeout);
        Assert.Equal(LockTimeout.Default, PageFileConfig.Large.LockTimeout);
        Assert.Equal(LockTimeout.Default, PageFileConfig.Server(Path.Combine(_dir, "server.db")).LockTimeout);

        foreach (var (name, config) in new[] { ("small", PageFileConfig.Small), ("large", PageFileConfig.Large) })
        {
            var path = Path.Combine(_dir, name + ".db");
            new BLiteEngine(path, config).Dispose();
            Assert.Equal(LockTimeout.Default, PageFileConfig.DetectFromFile(path)!.Value.LockTimeout);
        }

        using var memory = BLiteEngine.CreateInMemory();
        Assert.Equal(LockTimeout.Default, memory.Storage.LockTimeout);
    }

    /// <summary>
    /// A configured timeout reaches the WAL, the main, index and collection page files, the
    /// admission gate and both kinds of collection.
    /// </summary>
    [Fact]
    public async Task ConfiguredTimeout_ReachesEveryLock()
    {
        var timeout = LockTimeout.From(readTimeoutMs: 1_234, writeTimeoutMs: 2_345, maxConcurrentWriters: 7);
        var path = Path.Combine(_dir, "engine", "test.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var engine = new BLiteEngine(path, PageFileConfig.Server(path) with { LockTimeout = timeout }))
        {
            var col = engine.GetOrCreateCollection("docs");
            await col.InsertAsync(col.CreateDocument(["value"], b => b.AddInt32("value", 1)));
            var storage = engine.Storage;

            Assert.Equal(timeout, storage.LockTimeout);
            Assert.Equal(2_345, (int)Field(Field(storage, "_wal"), "_writeTimeoutMs"));
            Assert.Equal(timeout, PageFileLockTimeout(Field(storage, "_pageFile")));
            Assert.Equal(timeout, PageFileLockTimeout(Field(storage, "_indexFile")));
            var collectionFiles = (ConcurrentDictionary<string, Lazy<IPageStorage>>)Field(storage, "_collectionFiles");
            Assert.NotEmpty(collectionFiles);
            foreach (var file in collectionFiles.Values)
                Assert.Equal(timeout, PageFileLockTimeout(file.Value));
            Assert.Equal(7, ((SemaphoreSlim)Field(storage, "_writerGate")).CurrentCount);
            Assert.Equal(2_345, WriteLockTimeoutMs(col));
        }

        var ctxPath = Path.Combine(_dir, "context", "test.db");
        Directory.CreateDirectory(Path.GetDirectoryName(ctxPath)!);
        using (var db = new TestDbContext(ctxPath, PageFileConfig.Default with { LockTimeout = timeout }))
            Assert.Equal(2_345, WriteLockTimeoutMs(db.TestDocuments));
    }

    /// <summary>
    /// The commit lock waits for the configured write timeout, not for the default: with the lock
    /// held, an auto-committing insert fails after about 150 ms, well before the 5 s default.
    /// </summary>
    [Fact]
    public async Task CommitLock_WaitsForTheConfiguredWriteTimeout()
    {
        using var engine = new BLiteEngine(Path.Combine(_dir, "commit.db"),
            PageFileConfig.Default with { LockTimeout = LockTimeout.From(150, 150) });
        var col = engine.GetOrCreateCollection("docs");
        await col.InsertAsync(col.CreateDocument(["value"], b => b.AddInt32("value", 0)));

        var commitLock = (SemaphoreSlim)Field(engine.Storage, "_commitLock");
        await commitLock.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
                await col.InsertAsync(col.CreateDocument(["value"], b => b.AddInt32("value", 1))));
            sw.Stop();
            Assert.Contains("commit lock", ex.Message);
            Assert.InRange(sw.ElapsedMilliseconds, 100, 3_000);
        }
        finally
        {
            commitLock.Release();
        }
    }

    /// <summary>
    /// Writers on one collection queue behind each other's commits, and each commit here takes
    /// 150 ms to reach the WAL (a disk sync on a slow disk). The last of 8 writers waits about a
    /// second, twice the 500 ms the default used to allow, and none of them may time out.
    /// </summary>
    [Fact]
    public async Task WritersQueuedBehindSlowCommits_DoNotTimeOut()
    {
        const int writers = 8;
        const int commitMs = 150;
        var wal = new SlowFlushWriteAheadLog(new MemoryWriteAheadLog(), commitMs);
        var pages = new MemoryPageStorage(16384);
        pages.Open();
        using var engine = BLiteEngine.CreateFromStorage(new StorageEngine(pages, wal));
        var col = engine.GetOrCreateCollection("docs");
        await col.InsertAsync(col.CreateDocument(["value"], b => b.AddInt32("value", -1)));

        wal.Slow = true;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inserts = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            var sw = Stopwatch.StartNew();
            await col.InsertAsync(col.CreateDocument(["value"], b => b.AddInt32("value", w)));
            return sw.ElapsedMilliseconds;
        })).ToList();
        start.SetResult(true);
        var elapsed = await Task.WhenAll(inserts);

        Assert.True(elapsed.Max() > 500, $"the slowest writer took {elapsed.Max()} ms; the scenario must wait longer than the old 500 ms default");
        Assert.Equal(writers + 1, (await col.FindAllAsync().ToListAsync()).Count);
    }

    private static object Field(object owner, string name)
        => owner.GetType().GetField(name, Flags)!.GetValue(owner)!;

    private static LockTimeout PageFileLockTimeout(object pageFile)
        => ((PageFileConfig)Field(Assert.IsType<PageFile>(pageFile), "_config")).LockTimeout;

    private static int WriteLockTimeoutMs(object collection)
        => (int)collection.GetType().GetProperty("WriteLockTimeoutMs", Flags)!.GetValue(collection)!;

    /// <summary>Delays every flush, like a WAL whose commit waits for a slow disk sync.</summary>
    private sealed class SlowFlushWriteAheadLog(IWriteAheadLog inner, int delayMs) : IWriteAheadLog
    {
        public volatile bool Slow;

        public async Task FlushAsync(CancellationToken ct = default)
        {
            if (Slow) await Task.Delay(delayMs, ct).ConfigureAwait(false);
            await inner.FlushAsync(ct).ConfigureAwait(false);
        }

        public ValueTask WriteBeginRecordAsync(ulong transactionId, CancellationToken ct = default) => inner.WriteBeginRecordAsync(transactionId, ct);
        public ValueTask WriteCommitRecordAsync(ulong transactionId, CancellationToken ct = default) => inner.WriteCommitRecordAsync(transactionId, ct);
        public ValueTask WriteAbortRecordAsync(ulong transactionId, CancellationToken ct = default) => inner.WriteAbortRecordAsync(transactionId, ct);
        public ValueTask WriteDataRecordAsync(ulong transactionId, uint pageId, ReadOnlyMemory<byte> afterImage, CancellationToken ct = default)
            => inner.WriteDataRecordAsync(transactionId, pageId, afterImage, ct);
        public long GetCurrentSize() => inner.GetCurrentSize();
        public Task TruncateAsync(CancellationToken ct = default) => inner.TruncateAsync(ct);
        public List<WalRecord> ReadAll() => inner.ReadAll();
        public void Dispose() => inner.Dispose();
    }
}
