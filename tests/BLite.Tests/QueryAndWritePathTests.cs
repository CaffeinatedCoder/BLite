using System.Linq.Expressions;
using BLite.Bson;
using BLite.Core;
using BLite.Core.Indexing;
using BLite.Core.Query;
using BLite.Core.Storage;
using BLite.Core.Transactions;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Predicate overloads of LINQ terminals use the index planner, and the write path changes
/// (single-pass B-Tree insert, no defensive page copy) keep their semantics.
/// </summary>
public class QueryAndWritePathTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TestDbContext _db;
    private readonly List<PersonV2> _people;

    public QueryAndWritePathTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"query_write_{Guid.NewGuid():N}.db");
        _db = new TestDbContext(_dbPath);
        _people = Enumerable.Range(0, 300).Select(i => new PersonV2 { Name = $"p{i}", Age = i % 50 }).ToList();
        _db.PeopleV2.InsertBulkAsync(_people).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private static QueryModel Model(Expression expression)
    {
        var visitor = new BTreeExpressionVisitor();
        visitor.Visit(expression);
        return visitor.GetModel();
    }

    private IQueryable<PersonV2> Q => _db.PeopleV2.AsQueryable();

    // ── Query model ─────────────────────────────────────────────────────────

    [Fact]
    public void Model_PredicateOfCountAndElementTerminals_FoldsIntoWhere()
    {
        var count = Model(Expression.Call(typeof(Queryable), "Count", [typeof(PersonV2)], Q.Expression, Expression.Quote((Expression<Func<PersonV2, bool>>)(p => p.Age == 3))));
        Assert.True(count.IsCountOnly);
        Assert.False(count.HasComplexOperators);
        Assert.NotNull(count.WhereClause);

        foreach (var name in new[] { "First", "FirstOrDefault", "Single", "SingleOrDefault", "Any" })
        {
            var model = Model(Expression.Call(typeof(Queryable), name, [typeof(PersonV2)], Q.Expression, Expression.Quote((Expression<Func<PersonV2, bool>>)(p => p.Age == 3))));
            Assert.Equal(name, model.ElementTerminal);
            Assert.False(model.HasComplexOperators);
            Assert.NotNull(model.WhereClause);
        }
    }

    [Fact]
    public void Model_PredicateAfterTakeSkipOrSelect_IsNotFolded()
    {
        var afterTake = Model(Expression.Call(typeof(Queryable), "Count", [typeof(PersonV2)], Q.Take(5).Expression, Expression.Quote((Expression<Func<PersonV2, bool>>)(p => p.Age == 3))));
        var afterSkip = Model(Expression.Call(typeof(Queryable), "First", [typeof(PersonV2)], Q.Skip(5).Expression, Expression.Quote((Expression<Func<PersonV2, bool>>)(p => p.Age == 3))));
        var afterSelect = Model(Expression.Call(typeof(Queryable), "Any", [typeof(int)], Q.Select(p => p.Age).Expression, Expression.Quote((Expression<Func<int, bool>>)(a => a == 3))));

        Assert.True(afterTake.HasComplexOperators);
        Assert.Null(afterTake.WhereClause);
        Assert.True(afterSkip.HasComplexOperators);
        Assert.Null(afterSkip.WhereClause);
        Assert.True(afterSelect.HasComplexOperators);
        Assert.Null(afterSelect.WhereClause);
    }

    // ── Results ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_WithPredicate_MatchesLinqToObjects()
    {
        Assert.Equal(_people.Count(p => p.Age == 7), Q.Count(p => p.Age == 7));
        Assert.Equal(_people.Count(p => p.Age >= 10 && p.Age < 20), Q.Count(p => p.Age >= 10 && p.Age < 20));
        Assert.Equal(_people.Count(p => p.Name.EndsWith("9")), Q.Count(p => p.Name.EndsWith("9")));
        Assert.Equal(_people.LongCount(p => p.Age == 7), Q.LongCount(p => p.Age == 7));
        Assert.Equal(_people.OrderBy(p => p.Name).Take(40).Count(p => p.Age == 7), Q.OrderBy(p => p.Name).Take(40).Count(p => p.Age == 7));
    }

    [Fact]
    public void ElementTerminals_WithPredicate_MatchLinqToObjects()
    {
        Assert.Equal(7, Q.First(p => p.Age == 7).Age);
        Assert.Equal("p42", Q.FirstOrDefault(p => p.Name == "p42")!.Name);
        Assert.Null(Q.FirstOrDefault(p => p.Age == 999));
        Assert.Equal("p42", Q.Single(p => p.Name == "p42").Name);
        Assert.Null(Q.SingleOrDefault(p => p.Name == "nobody"));
        Assert.True(Q.Any(p => p.Age == 49));
        Assert.False(Q.Any(p => p.Age == 50));
        Assert.True(Q.Any());

        Assert.Throws<InvalidOperationException>(() => Q.First(p => p.Age == 999));
        Assert.Throws<InvalidOperationException>(() => Q.Single(p => p.Age == 7));
        Assert.Throws<InvalidOperationException>(() => Q.SingleOrDefault(p => p.Age == 7));
        Assert.Throws<InvalidOperationException>(() => Q.Single(p => p.Age == 999));
    }

    [Fact]
    public void ElementTerminals_AfterOtherOperators_KeepTheirSemantics()
    {
        Assert.Equal(_people.OrderByDescending(p => p.Name).First(p => p.Age == 7).Name,
                     Q.OrderByDescending(p => p.Name).First(p => p.Age == 7).Name);
        Assert.Equal(_people.Where(p => p.Age > 5).FirstOrDefault(p => p.Age < 8)!.Age,
                     Q.Where(p => p.Age > 5).FirstOrDefault(p => p.Age < 8)!.Age);
        Assert.Equal(_people.OrderBy(p => p.Name).Skip(290).Any(p => p.Age == 7), Q.OrderBy(p => p.Name).Skip(290).Any(p => p.Age == 7));
        Assert.True(Q.All(p => p.Age < 50));

        var fallback = new PersonV2 { Name = "fallback" };
        Assert.Same(fallback, Q.FirstOrDefault(p => p.Age == 999, fallback));
    }

    // ── Write path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AddWrite_CallerMayReuseItsBufferRightAway()
    {
        var page = _db.Storage.AllocatePage();
        var buffer = new byte[_db.Storage.PageSize];
        buffer.AsSpan(100, 4).Fill(0xAB);

        using (var txn = _db.Storage.BeginTransaction())
        {
            txn.AddWrite(new WriteOperation(ObjectId.Empty, buffer.AsMemory(), page, OperationType.Insert));
            buffer.AsSpan().Clear(); // e.g. returned to a pool and reused

            var read = new byte[_db.Storage.PageSize];
            _db.Storage.ReadPage(page, txn.TransactionId, read);
            Assert.All(read.AsSpan(100, 4).ToArray(), b => Assert.Equal(0xAB, b));
            await txn.CommitAsync();
        }

        var committed = new byte[_db.Storage.PageSize];
        _db.Storage.ReadPage(page, null, committed);
        Assert.All(committed.AsSpan(100, 4).ToArray(), b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public async Task UniqueBTree_SinglePassInsert_RejectsDuplicatesAndIgnoresExactReinserts()
    {
        var tree = new BTreeIndex(_db.Storage, IndexOptions.CreateUnique("_id"));
        var keys = Enumerable.Range(0, 500).Select(_ => new IndexKey(ObjectId.NewObjectId())).ToArray();

        using var txn = _db.Storage.BeginTransaction();
        for (int i = 0; i < keys.Length; i++)
            tree.Insert(keys[i], new DocumentLocation((uint)(i + 1), 0), txn.TransactionId);

        tree.Insert(keys[123], new DocumentLocation(124, 0), txn.TransactionId); // same key and location: no-op
        Assert.Throws<InvalidOperationException>(() => tree.Insert(keys[123], new DocumentLocation(999, 0), txn.TransactionId));

        Assert.Equal(500, tree.Range(IndexKey.MinKey, IndexKey.MaxKey, IndexDirection.Forward, txn.TransactionId).Count());
        Assert.True(tree.TryFind(keys[123], out var location, txn.TransactionId));
        Assert.Equal(124u, location.PageId);
        await txn.CommitAsync();
    }
}
