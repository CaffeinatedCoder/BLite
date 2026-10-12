using BLite.Bson;
using BLite.Core;
using BLite.Core.Indexing;
using BLite.Core.Storage;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// A freed page must not be handed out again while the WAL still holds a committed image of
/// it: reads prefer the WAL, so the reused page would read back its old contents (#168).
/// </summary>
public class FreePageReuseTests : IDisposable
{
    private readonly string _dbPath;

    public FreePageReuseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"freepage_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        TryDelete(_dbPath);
        TryDelete(Path.ChangeExtension(_dbPath, ".wal"));
        var collDir = _dbPath + "_coll";
        if (Directory.Exists(collDir))
            try { Directory.Delete(collDir, recursive: true); } catch { /* best-effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task DropAndRecreateIndex_RangeScan_ReturnsEachDocumentOnce()
    {
        using var db = new TestDbContext(_dbPath);
        for (int i = 0; i < 200; i++)
            await db.PeopleV2.InsertAsync(new PersonV2 { Name = $"p{i}", Age = i % 50 });

        for (int pass = 0; pass < 4; pass++)
        {
            await db.PeopleV2.DropIndexAsync("idx_Age");
            await db.PeopleV2.CreateIndexAsync(p => p.Age, "idx_Age");

            Assert.Equal(200, (await db.PeopleV2.QueryIndexAsync("idx_Age", null, null).ToListAsync()).Count);
        }

        Assert.Equal(200, db.PeopleV2.AsQueryable().Where(p => p.Age >= 0).ToList().Count);
        Assert.Equal(4, db.PeopleV2.AsQueryable().Where(p => p.Age == 49).ToList().Count);
    }

    [Fact]
    public async Task FreedPage_IsReusedOnlyAfterWalTruncation_AndReadsItsNewContents()
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var page = await AllocateAndCommitAsync(storage, fill: 0xAA);

        storage.FreePage(page);
        Assert.NotEqual(page, storage.AllocatePage());

        await storage.CheckpointAsync();
        Assert.Equal(page, storage.AllocatePage());

        var fresh = Filled(storage.PageSize, 0xBB);
        storage.WritePageImmediate(page, fresh);
        var read = new byte[storage.PageSize];
        storage.ReadPage(page, null, read);
        Assert.Equal(fresh, read);
    }

    [Fact]
    public async Task FreedPage_WithEmptyWalIndex_IsReleasedByCheckpoint()
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var page = await AllocateAndCommitAsync(storage, fill: 0xAA);
        await storage.CheckpointAsync();

        storage.FreePage(page);
        await storage.CheckpointAsync();

        Assert.Equal(page, storage.AllocatePage());
    }

    [Fact]
    public async Task FreedPage_StillQueuedAtDispose_IsNotLeaked()
    {
        uint page;
        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            page = await AllocateAndCommitAsync(storage, fill: 0xAA);
            storage.FreePage(page);
        }

        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
            Assert.Equal(page, storage.AllocatePage());
    }

    [Fact]
    public async Task DroppedCollectionFile_DiscardsItsQueuedFrees()
    {
        var config = PageFileConfig.Default with { CollectionDataDirectory = _dbPath + "_coll" };
        using var storage = new StorageEngine(_dbPath, config);

        var oldPage = storage.AllocateCollectionPage("items");
        await WriteAndCommitAsync(storage, oldPage, fill: 0xAA);
        storage.FreePage(oldPage);
        storage.DropCollectionFile("items");

        // The recreated file reuses the slot, so its first page gets the same encoded ID.
        var livePage = storage.AllocateCollectionPage("items");
        await WriteAndCommitAsync(storage, livePage, fill: 0xCC);
        await storage.CheckpointAsync();

        // Releasing the stale free would have put the live page on the new file's free list.
        Assert.NotEqual(livePage, storage.AllocateCollectionPage("items"));
    }

    [Fact]
    public async Task FreedPage_WrittenByAnOpenTransaction_IsNotReleasedBeforeThatCommitIsCheckpointed()
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var page = storage.AllocatePage();

        using (var txn = storage.BeginTransaction())
        {
            storage.WritePage(page, txn.TransactionId, Filled(storage.PageSize, 0xAA));
            storage.FreePage(page);

            // A truncation while the transaction is open must not release the page: its image
            // only reaches the WAL when the transaction commits.
            await storage.CheckpointAsync();
            Assert.NotEqual(page, storage.AllocatePage());

            await txn.CommitAsync();
        }
        Assert.NotEqual(page, storage.AllocatePage());

        // The checkpoint after the commit flushes that image and truncates; now the page is free.
        await storage.CheckpointAsync();
        Assert.Equal(page, storage.AllocatePage());

        var fresh = Filled(storage.PageSize, 0xBB);
        storage.WritePageImmediate(page, fresh);
        var read = new byte[storage.PageSize];
        storage.ReadPage(page, null, read);
        Assert.Equal(fresh, read);
    }

    [Fact]
    public async Task BTreeMergesInAnOpenTransaction_SurviveACheckpointBeforeTheCommit()
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var tree = new BTreeIndex(storage, IndexOptions.CreateUnique("_id"));
        var keys = Enumerable.Range(0, 400).Select(_ => new IndexKey(ObjectId.NewObjectId())).OrderBy(k => k).ToArray();
        using (var txn = storage.BeginTransaction())
        {
            for (int i = 0; i < keys.Length; i++)
                tree.Insert(keys[i], new DocumentLocation((uint)(i + 1), 0), txn.TransactionId);
            await txn.CommitAsync();
        }

        // Deleting most keys merges leaves, which frees their pages inside the transaction.
        using (var txn = storage.BeginTransaction())
        {
            for (int i = 0; i < 350; i++)
                tree.Delete(keys[i], new DocumentLocation((uint)(i + 1), 0), txn.TransactionId);
            await storage.CheckpointAsync();
            await txn.CommitAsync();
        }

        // A second tree allocates pages; none of them may still belong to the first one.
        var other = new BTreeIndex(storage, IndexOptions.CreateUnique("_id"));
        var otherKeys = Enumerable.Range(0, 400).Select(_ => new IndexKey(ObjectId.NewObjectId())).ToArray();
        using (var txn = storage.BeginTransaction())
        {
            for (int i = 0; i < otherKeys.Length; i++)
                other.Insert(otherKeys[i], new DocumentLocation((uint)(i + 1000), 0), txn.TransactionId);
            await txn.CommitAsync();
        }
        await storage.CheckpointAsync();

        Assert.Equal(keys.Skip(350), tree.Range(IndexKey.MinKey, IndexKey.MaxKey).Select(e => e.Key));
        Assert.Equal(400, other.Range(IndexKey.MinKey, IndexKey.MaxKey).Count());
    }

    [Fact]
    public async Task MultiProcess_Truncation_KeepsRecordsAnotherProcessHasNotReplayed()
    {
        var dir = _dbPath + "_mp";
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "shared.db");
        var walPath = Path.ChangeExtension(dbPath, ".wal");
        var cfg = PageFileConfig.Default with { AllowMultiProcessAccess = true };
        try
        {
            using var engineA = new StorageEngine(dbPath, cfg);
            using var engineB = new StorageEngine(dbPath, cfg);

            // A queues a free (every free is queued in multi-process mode) without replaying B.
            engineA.FreePage(engineA.AllocatePage());

            using (var txB = engineB.BeginTransaction())
            {
                var page = engineB.AllocatePage();
                engineB.WritePage(page, txB.TransactionId, Filled(engineB.PageSize, 0xCC));
                await txB.CommitAsync();
            }
            long walAfterB = new FileInfo(walPath).Length;
            Assert.True(walAfterB > 0);

            // A's WAL index is empty, but B's committed records must stay in the shared WAL.
            await engineA.CheckpointAsync();
            Assert.Equal(walAfterB, new FileInfo(walPath).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task MultiProcess_CommitAfterAnotherProcessCommitted_CheckpointKeepsThatCommit()
    {
        var dir = _dbPath + "_mp";
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "shared.db");
        var cfg = PageFileConfig.Default with { AllowMultiProcessAccess = true };
        try
        {
            using var engineA = new StorageEngine(dbPath, cfg);
            using var engineB = new StorageEngine(dbPath, cfg);
            var pageA = engineA.AllocatePage();
            var pageB = engineA.AllocatePage();

            // B begins before A commits, so its begin-time replay does not see A's record.
            using (var txB = engineB.BeginTransaction())
            {
                await WriteAndCommitAsync(engineA, pageA, fill: 0xAA);

                engineB.WritePage(pageB, txB.TransactionId, Filled(engineB.PageSize, 0xBB));
                await txB.CommitAsync();
            }

            // B's commit must not have skipped (or overwritten) A's record: after B checkpoints,
            // A's page is still there for B and for a process that opens the database now.
            await engineB.CheckpointAsync();

            AssertPage(engineB, pageA, 0xAA);
            AssertPage(engineB, pageB, 0xBB);
            using var engineC = new StorageEngine(dbPath, cfg);
            using (engineC.BeginTransaction())
            {
                AssertPage(engineC, pageA, 0xAA);
                AssertPage(engineC, pageB, 0xBB);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task MultiProcess_PageCommittedByBothProcesses_CheckpointKeepsTheNewerImage()
    {
        var dir = _dbPath + "_mp";
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "shared.db");
        var cfg = PageFileConfig.Default with { AllowMultiProcessAccess = true };
        try
        {
            using var engineA = new StorageEngine(dbPath, cfg);
            using var engineB = new StorageEngine(dbPath, cfg);
            var page = engineA.AllocatePage();

            await WriteAndCommitAsync(engineB, page, fill: 0xBB);
            await WriteAndCommitAsync(engineA, page, fill: 0xAA);

            // B replays A's newer image of the page it has committed itself, then checkpoints.
            using (engineB.BeginTransaction())
                AssertPage(engineB, page, 0xAA);
            await engineB.CheckpointAsync();

            AssertPage(engineB, page, 0xAA);
            using var engineC = new StorageEngine(dbPath, cfg);
            using (engineC.BeginTransaction())
                AssertPage(engineC, page, 0xAA);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertPage(StorageEngine storage, uint page, byte fill)
    {
        var read = new byte[storage.PageSize];
        storage.ReadPage(page, null, read);
        Assert.Equal(Filled(storage.PageSize, fill), read);
    }

    private static async Task<uint> AllocateAndCommitAsync(StorageEngine storage, byte fill)
    {
        var page = storage.AllocatePage();
        await WriteAndCommitAsync(storage, page, fill);
        return page;
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
