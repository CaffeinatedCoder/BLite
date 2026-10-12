using BLite.Core.Storage;

namespace BLite.Tests;

/// <summary>
/// Multi-process mode: once another process has checkpointed and truncated the shared WAL, the
/// pages this engine replayed from it are older than the page file and must never be read or
/// checkpointed again (#197). Two engines on the same files stand in for two processes.
/// </summary>
public class MultiProcessWalTruncationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private static readonly PageFileConfig Config = PageFileConfig.Default with { AllowMultiProcessAccess = true };

    public MultiProcessWalTruncationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"blite_mp_truncation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "shared.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private long WalLength => new FileInfo(Path.ChangeExtension(_dbPath, ".wal")).Length;

    [Fact]
    public async Task ReplayedPage_AfterAnotherProcessTruncated_IsNotWrittenBackOnCheckpoint()
    {
        // The sequence from #197.
        using var a = new StorageEngine(_dbPath, Config);
        using var b = new StorageEngine(_dbPath, Config);
        var page = a.AllocatePage();

        await WriteAndCommitAsync(a, page, 0x11);
        using (b.BeginTransaction())
            AssertPage(b, page, 0x11); // B replays 0x11

        await WriteAndCommitAsync(a, page, 0x22);
        await a.CheckpointAsync();
        Assert.Equal(0, WalLength);    // A truncated the WAL

        using (b.BeginTransaction())
            AssertPage(b, page, 0x22);
        await b.CheckpointAsync();

        AssertPage(b, page, 0x22);
        AssertFreshEngineReads(page, 0x22);
    }

    [Fact]
    public async Task ReplayedPage_AfterAnotherProcessTruncated_IsNotWrittenBack_EvenWithoutANewTransaction()
    {
        using var a = new StorageEngine(_dbPath, Config);
        using var b = new StorageEngine(_dbPath, Config);
        var page = a.AllocatePage();

        await WriteAndCommitAsync(a, page, 0x11);
        using (b.BeginTransaction())
            AssertPage(b, page, 0x11);

        await WriteAndCommitAsync(a, page, 0x22);
        await a.CheckpointAsync();
        Assert.Equal(0, WalLength);

        // B checkpoints straight away, without a transaction that would catch up first.
        await b.CheckpointAsync();

        AssertFreshEngineReads(page, 0x22);
        AssertPage(b, page, 0x22);
    }

    [Fact]
    public async Task WalRegrownPastTheReplayPosition_IsReplayedFromItsStart()
    {
        using var a = new StorageEngine(_dbPath, Config);
        using var b = new StorageEngine(_dbPath, Config);
        var first = a.AllocatePage();
        var second = a.AllocatePage();
        var third = a.AllocatePage();

        await WriteAndCommitAsync(a, first, 0x11);
        using (b.BeginTransaction())
            AssertPage(b, first, 0x11); // B's replay position: the end of one transaction
        long bPosition = WalLength;

        await a.CheckpointAsync();
        Assert.Equal(0, WalLength);

        // A appends more than B had replayed, so the new WAL's end passes B's old position.
        await WriteAndCommitAsync(a, second, 0x22);
        await WriteAndCommitAsync(a, third, 0x33);
        Assert.True(WalLength > bPosition);

        // B catches up without reading the pages (a read would fetch them from the WAL on its
        // own). Replaying from B's old position would skip the new WAL's first record; B would
        // then believe it holds everything, checkpoint, and truncate that record away.
        using (b.BeginTransaction()) { }
        await b.CheckpointAsync();
        AssertFreshEngineReads(first, 0x11);
        AssertFreshEngineReads(second, 0x22);
        AssertFreshEngineReads(third, 0x33);
    }

    [Fact]
    public async Task OwnCommit_AfterAnotherProcessTruncated_IsKept()
    {
        using var a = new StorageEngine(_dbPath, Config);
        using var b = new StorageEngine(_dbPath, Config);
        var shared = a.AllocatePage();
        var own = a.AllocatePage();

        await WriteAndCommitAsync(a, shared, 0x11);
        using (b.BeginTransaction())
            AssertPage(b, shared, 0x11);

        await WriteAndCommitAsync(a, shared, 0x22);
        await a.CheckpointAsync();
        Assert.Equal(0, WalLength);

        // B commits without beginning in between; its commit lands in the new WAL.
        using (var tx = b.BeginTransaction())
        {
            b.WritePage(own, tx.TransactionId, Filled(b.PageSize, 0xBB));
            await tx.CommitAsync();
        }
        AssertPage(b, own, 0xBB);
        AssertPage(b, shared, 0x22);

        await b.CheckpointAsync();
        AssertFreshEngineReads(shared, 0x22);
        AssertFreshEngineReads(own, 0xBB);
    }

    [Fact]
    public async Task TruncationByABackup_IsSeenByTheOtherProcess()
    {
        using var a = new StorageEngine(_dbPath, Config);
        using var b = new StorageEngine(_dbPath, Config);
        var page = a.AllocatePage();

        await WriteAndCommitAsync(a, page, 0x11);
        using (b.BeginTransaction())
            AssertPage(b, page, 0x11);
        await WriteAndCommitAsync(a, page, 0x22);

        // A backup checkpoints and truncates the WAL, like a checkpoint does.
        await a.BackupAsync(Path.Combine(_dir, "backup.db"));
        Assert.Equal(0, WalLength);

        await b.CheckpointAsync();
        AssertFreshEngineReads(page, 0x22);
        AssertPage(b, page, 0x22);
    }

    [Fact]
    public async Task RollbackAfterAnotherProcessTruncated_LeavesNoGapThatHidesLaterCommits()
    {
        using var a = new StorageEngine(_dbPath, Config);
        using var b = new StorageEngine(_dbPath, Config);
        var p1 = a.AllocatePage();
        var p2 = a.AllocatePage();
        var p3 = a.AllocatePage();

        // A writes two transactions, so its own write position is past one transaction's length.
        await WriteAndCommitAsync(a, p1, 0x11);
        await WriteAndCommitAsync(a, p1, 0x12);
        using (b.BeginTransaction()) { }
        await b.CheckpointAsync();
        Assert.Equal(0, WalLength);
        await WriteAndCommitAsync(b, p2, 0x22);

        // A rolls back. An abort record written at A's stale position would leave a gap of zeros
        // after B's record, and B's replay would stop there and miss A's next commit.
        using (a.BeginTransaction()) { }
        await WriteAndCommitAsync(a, p3, 0x33);

        await b.CheckpointAsync();
        AssertFreshEngineReads(p1, 0x12);
        AssertFreshEngineReads(p2, 0x22);
        AssertFreshEngineReads(p3, 0x33);
    }

    [Fact]
    public async Task RandomInterleavings_KeepEveryCommit()
    {
        // Seeded, so a failure reproduces. Commits, empty and rolled-back transactions, reads and
        // checkpoints (each of which may truncate the WAL) from either engine.
        for (int seed = 0; seed < 25; seed++)
        {
            var dbPath = Path.Combine(_dir, $"stress_{seed}.db");
            var rnd = new Random(seed);
            var expected = new Dictionary<uint, byte>();
            using (var a = new StorageEngine(dbPath, Config))
            using (var b = new StorageEngine(dbPath, Config))
            {
                var engines = new[] { a, b };
                var pages = Enumerable.Range(0, 8).Select(_ => a.AllocatePage()).ToArray();
                for (int step = 0; step < 300; step++)
                {
                    var engine = engines[rnd.Next(2)];
                    var page = pages[rnd.Next(pages.Length)];
                    switch (rnd.Next(6))
                    {
                        case 0:
                        case 1:
                            var fill = (byte)rnd.Next(1, 255);
                            await WriteAndCommitAsync(engine, page, fill);
                            expected[page] = fill;
                            break;
                        case 2:
                            using (engine.BeginTransaction()) { }
                            break;
                        case 3:
                            using (var tx = engine.BeginTransaction())
                                engine.WritePage(page, tx.TransactionId, Filled(engine.PageSize, 0xEE)); // rolled back
                            break;
                        case 4:
                            await engine.CheckpointAsync();
                            break;
                        case 5:
                            using (engine.BeginTransaction())
                                if (expected.TryGetValue(page, out var current))
                                    AssertPage(engine, page, current);
                            break;
                    }
                }
                await a.CheckpointAsync();
                await b.CheckpointAsync();
                await a.CheckpointAsync();
            }

            using var c = new StorageEngine(dbPath, Config);
            using (c.BeginTransaction())
                foreach (var (page, fill) in expected)
                    AssertPage(c, page, fill);
        }
    }

    private void AssertFreshEngineReads(uint page, byte fill)
    {
        using var c = new StorageEngine(_dbPath, Config);
        using (c.BeginTransaction())
            AssertPage(c, page, fill);
    }

    private static void AssertPage(StorageEngine storage, uint page, byte fill)
    {
        var read = new byte[storage.PageSize];
        storage.ReadPage(page, null, read);
        Assert.Equal(Filled(storage.PageSize, fill)[32], read[32]);
        Assert.Equal(Filled(storage.PageSize, fill), read);
    }

    private static async Task WriteAndCommitAsync(StorageEngine storage, uint page, byte fill)
    {
        using var txn = storage.BeginTransaction();
        storage.WritePage(page, txn.TransactionId, Filled(storage.PageSize, fill));
        await txn.CommitAsync();
    }

    private static byte[] Filled(int size, byte value)
    {
        var buffer = new byte[size];
        buffer.AsSpan(32).Fill(value); // keep a zeroed page header
        return buffer;
    }
}
