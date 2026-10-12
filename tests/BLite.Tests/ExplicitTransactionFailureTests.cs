using BLite.Bson;
using BLite.Core;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// A write that fails inside a caller-owned transaction must leave that transaction
/// usable: only the failed operation is rejected, the transaction is not rolled back
/// behind the caller's back, and a later <c>CommitAsync</c> persists the other writes.
/// Insert/Upsert used to roll the caller's transaction back on any exception (#185),
/// while Update only rolled back a transaction it had opened itself.
/// </summary>
public class ExplicitTransactionFailureTests : IDisposable
{
    private readonly string _dbPath;

    public ExplicitTransactionFailureTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"blite_tx_failure_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var p in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal"), _dbPath + "-shm" })
            if (File.Exists(p)) File.Delete(p);
    }

    // ── Typed collections ────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var db = new TestDbContext(_dbPath);
        using var tx = await db.BeginTransactionAsync();

        await db.AsyncDocs.InsertAsync(new AsyncDoc { Id = 1, Name = "first" }, tx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await db.AsyncDocs.InsertAsync(new AsyncDoc { Id = 1, Name = "duplicate" }, tx));
        Assert.Contains("Duplicate key violation", ex.Message);

        // The transaction is still usable after the rejected write.
        await db.AsyncDocs.InsertAsync(new AsyncDoc { Id = 2, Name = "second" }, tx);
        await tx.CommitAsync();

        Assert.Equal("first", (await db.AsyncDocs.FindByIdAsync(1))?.Name);
        Assert.Equal("second", (await db.AsyncDocs.FindByIdAsync(2))?.Name);
    }

    [Fact]
    public async Task InsertBulkAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var db = new TestDbContext(_dbPath);
        using var tx = await db.BeginTransactionAsync();

        await db.AsyncDocs.InsertAsync(new AsyncDoc { Id = 1, Name = "first" }, tx);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await db.AsyncDocs.InsertBulkAsync(
                [new AsyncDoc { Id = 1, Name = "duplicate" }], tx));

        await tx.CommitAsync();

        Assert.Equal("first", (await db.AsyncDocs.FindByIdAsync(1))?.Name);
    }

    [Fact]
    public async Task UpsertAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var db = new TestDbContext(_dbPath);
        using var tx = await db.BeginTransactionAsync();

        // idx_vector is declared with 3 dimensions on VectorEntity.
        var good = new VectorEntity { Title = "good", Embedding = [1f, 2f, 3f] };
        await db.VectorItems.UpsertAsync(good, tx);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await db.VectorItems.UpsertAsync(
                new VectorEntity { Title = "bad", Embedding = [1f, 2f] }, tx));

        await tx.CommitAsync();

        Assert.Equal("good", (await db.VectorItems.FindByIdAsync(good.Id))?.Title);
    }

    [Fact]
    public async Task UpsertBulkAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var db = new TestDbContext(_dbPath);
        using var tx = await db.BeginTransactionAsync();

        var good = new VectorEntity { Title = "good", Embedding = [1f, 2f, 3f] };
        await db.VectorItems.UpsertAsync(good, tx);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await db.VectorItems.UpsertBulkAsync(
                [new VectorEntity { Title = "bad", Embedding = [1f, 2f] }], tx));

        await tx.CommitAsync();

        Assert.Equal("good", (await db.VectorItems.FindByIdAsync(good.Id))?.Title);
    }

    [Fact]
    public async Task Implicit_Transaction_Is_Still_Rolled_Back_On_Failure()
    {
        using var db = new TestDbContext(_dbPath);

        await db.AsyncDocs.InsertAsync(new AsyncDoc { Id = 1, Name = "first" });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await db.AsyncDocs.InsertAsync(new AsyncDoc { Id = 1, Name = "duplicate" }));

        // A later auto-committed write must not be blocked by a dangling transaction.
        await db.AsyncDocs.InsertAsync(new AsyncDoc { Id = 2, Name = "second" });

        Assert.Equal("first", (await db.AsyncDocs.FindByIdAsync(1))?.Name);
        Assert.Equal("second", (await db.AsyncDocs.FindByIdAsync(2))?.Name);
    }

    // ── Dynamic collections ──────────────────────────────────────────────────

    [Fact]
    public async Task Dynamic_InsertAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("docs", BsonIdType.Int32);
        var tx = await engine.BeginTransactionAsync();

        await col.InsertAsync(Doc(col, 1, "first"), tx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await col.InsertAsync(Doc(col, 1, "duplicate"), tx));
        Assert.Contains("Duplicate key violation", ex.Message);

        await col.InsertAsync(Doc(col, 2, "second"), tx);
        await tx.CommitAsync();

        Assert.Equal("first", await NameOf(col, 1));
        Assert.Equal("second", await NameOf(col, 2));
    }

    [Fact]
    public async Task Dynamic_InsertBulkAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("docs", BsonIdType.Int32);
        var tx = await engine.BeginTransactionAsync();

        await col.InsertAsync(Doc(col, 1, "first"), tx);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await col.InsertBulkAsync([Doc(col, 1, "duplicate")], tx));

        await tx.CommitAsync();

        Assert.Equal("first", await NameOf(col, 1));
    }

    [Fact]
    public async Task Dynamic_UpsertAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("vecs", BsonIdType.Int32);
        await col.CreateVectorIndexAsync("emb", dimensions: 3);
        var tx = await engine.BeginTransactionAsync();

        await col.UpsertAsync(VecDoc(col, 1, [1f, 2f, 3f]), tx);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await col.UpsertAsync(VecDoc(col, 2, [1f, 2f]), tx));

        await tx.CommitAsync();

        Assert.NotNull(await col.FindByIdAsync((BsonId)1));
    }

    [Fact]
    public async Task Dynamic_UpdateAsync_Failure_Keeps_Callers_Transaction_Alive()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("vecs", BsonIdType.Int32);
        await col.CreateVectorIndexAsync("emb", dimensions: 3);
        await col.InsertAsync(VecDoc(col, 1, [1f, 2f, 3f]));
        await col.InsertAsync(VecDoc(col, 2, [4f, 5f, 6f]));
        await engine.CommitAsync();

        var tx = await engine.BeginTransactionAsync();
        Assert.True(await col.UpdateAsync((BsonId)1, VecDoc(col, 1, [7f, 8f, 9f], "updated"), tx));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await col.UpdateAsync((BsonId)2, VecDoc(col, 2, [1f, 2f]), tx));

        await tx.CommitAsync();

        var updated = await col.FindByIdAsync((BsonId)1);
        Assert.NotNull(updated);
        Assert.True(updated.TryGetString("tag", out var tag));
        Assert.Equal("updated", tag);
    }

    private static BsonDocument Doc(DynamicCollection col, int id, string name) =>
        col.CreateDocument(["_id", "name"], b => b.AddId((BsonId)id).AddString("name", name));

    private static BsonDocument VecDoc(DynamicCollection col, int id, float[] emb, string tag = "orig") =>
        col.CreateDocument(["_id", "emb", "tag"], b => b.AddId((BsonId)id).AddFloatArray("emb", emb).AddString("tag", tag));

    private static async Task<string?> NameOf(DynamicCollection col, int id)
    {
        var doc = await col.FindByIdAsync((BsonId)id);
        return doc != null && doc.TryGetString("name", out var name) ? name : null;
    }
}
