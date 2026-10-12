using BLite.Core;
using BLite.Core.Query;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Regression tests for issue #176: unique secondary indexes on typed collections accepted
/// duplicate values. Typed indexes store <c>[user key][document id]</c>, so the BTree's own
/// unique check never fired; the collection now checks the user key before writing.
/// </summary>
public class UniqueIndexTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _dynamicDbPath;
    private readonly TestDbContext _db;

    public UniqueIndexTests()
    {
        var id = Guid.NewGuid();
        _dbPath = Path.Combine(Path.GetTempPath(), $"unique_index_{id}.db");
        _dynamicDbPath = Path.Combine(Path.GetTempPath(), $"unique_index_dyn_{id}.db");
        _db = new TestDbContext(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dynamicDbPath })
        {
            if (File.Exists(path)) File.Delete(path);
            var wal = Path.ChangeExtension(path, ".wal");
            if (File.Exists(wal)) File.Delete(wal);
        }
    }

    [Fact]
    public async Task Typed_Unique_Index_Rejects_Duplicates()
    {
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, "idx_name", unique: true);
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "dup", Age = 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _db.PeopleV2.InsertAsync(new PersonV2 { Name = "dup", Age = 2 }).AsTask());

        // The rejected insert leaves nothing behind.
        Assert.Equal(1, await _db.PeopleV2.CountAsync());
        var matches = await _db.PeopleV2.AsQueryable().Where(p => p.Name == "dup").ToListAsync();
        Assert.Single(matches);
        Assert.Equal(1, matches[0].Age);
    }

    [Fact]
    public async Task Dynamic_Unique_Index_Rejects_Duplicates()
    {
        using var engine = new BLiteEngine(_dynamicDbPath);
        var col = engine.GetOrCreateCollection("people");
        await col.CreateIndexAsync("name", unique: true);
        await col.InsertAsync(col.CreateDocument(["name"], b => b.AddString("name", "dup")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => col.InsertAsync(col.CreateDocument(["name"], b => b.AddString("name", "dup"))).AsTask());
    }

    [Fact]
    public async Task Typed_Unique_Index_Rejects_Update_Onto_Existing_Value()
    {
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, "idx_name", unique: true);
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "alice", Age = 1 });
        var bobId = await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "bob", Age = 2 });

        var bob = (await _db.PeopleV2.FindByIdAsync(bobId))!;
        bob.Name = "alice";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _db.PeopleV2.UpdateAsync(bob).AsTask());

        // The rejected update leaves the document and its index entry unchanged.
        var stored = await _db.PeopleV2.FindByIdAsync(bobId);
        Assert.Equal("bob", stored!.Name);
        Assert.Single(await _db.PeopleV2.AsQueryable().Where(p => p.Name == "bob").ToListAsync());
        Assert.Single(await _db.PeopleV2.AsQueryable().Where(p => p.Name == "alice").ToListAsync());
    }

    [Fact]
    public async Task Typed_Unique_Index_Allows_Update_That_Keeps_Or_Frees_A_Value()
    {
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, "idx_name", unique: true);
        var aliceId = await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "alice", Age = 1 });

        // Same key, other fields changed: the document's own entry is not a conflict.
        var alice = (await _db.PeopleV2.FindByIdAsync(aliceId))!;
        alice.Age = 42;
        Assert.True(await _db.PeopleV2.UpdateAsync(alice));

        // Moving the value away frees it for another document.
        alice.Name = "alicia";
        Assert.True(await _db.PeopleV2.UpdateAsync(alice));
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "alice", Age = 2 });

        Assert.Equal(2, await _db.PeopleV2.CountAsync());
    }

    [Fact]
    public async Task Typed_Unique_Index_Allows_Value_Again_After_Delete()
    {
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, "idx_name", unique: true);
        var id = await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "dup", Age = 1 });
        await _db.PeopleV2.DeleteAsync(id);

        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "dup", Age = 2 });

        Assert.Equal(1, await _db.PeopleV2.CountAsync());
    }

    [Fact]
    public async Task Typed_Unique_Index_Does_Not_Confuse_Values_Sharing_A_Prefix()
    {
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, "idx_name", unique: true);
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "abc", Age = 1 });
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "ab", Age = 2 });
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "a", Age = 3 });

        Assert.Equal(3, await _db.PeopleV2.CountAsync());
    }

    [Fact]
    public async Task Typed_Unique_Index_With_String_Ids_Rejects_Duplicates_But_Not_Prefixes()
    {
        await _db.StringEntities.CreateIndexAsync(e => e.Value, "idx_value", unique: true);
        await _db.StringEntities.InsertAsync(new StringEntity { Id = "1", Value = "abc" });
        await _db.StringEntities.InsertAsync(new StringEntity { Id = "2", Value = "ab" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _db.StringEntities.InsertAsync(new StringEntity { Id = "3", Value = "ab" }).AsTask());

        Assert.Equal(2, await _db.StringEntities.CountAsync());
    }

    [Fact]
    public async Task Typed_Unique_Index_Ignores_Null_Values()
    {
        await _db.StringEntities.CreateIndexAsync(e => e.Value, "idx_value", unique: true);
        await _db.StringEntities.InsertAsync(new StringEntity { Id = "1", Value = null });
        await _db.StringEntities.InsertAsync(new StringEntity { Id = "2", Value = null });

        Assert.Equal(2, await _db.StringEntities.CountAsync());
    }

    [Fact]
    public async Task Typed_Unique_Index_Rejects_Duplicates_Within_A_Bulk_Insert()
    {
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, "idx_name", unique: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.PeopleV2.InsertBulkAsync(
        [
            new PersonV2 { Name = "x", Age = 1 },
            new PersonV2 { Name = "dup", Age = 2 },
            new PersonV2 { Name = "dup", Age = 3 },
        ]).AsTask());

        Assert.Equal(0, await _db.PeopleV2.CountAsync());
    }
}
