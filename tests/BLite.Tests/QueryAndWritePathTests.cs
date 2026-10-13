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

        Assert.NotNull(count.FoldedTerminal);

        foreach (var name in new[] { "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "Any" })
        {
            var model = Model(Expression.Call(typeof(Queryable), name, [typeof(PersonV2)], Q.Expression, Expression.Quote((Expression<Func<PersonV2, bool>>)(p => p.Age == 3))));
            Assert.Equal(name, model.ElementTerminal);
            Assert.False(model.HasComplexOperators);
            Assert.NotNull(model.WhereClause);
            Assert.NotNull(model.FoldedTerminal);
        }
    }

    [Fact]
    public void Model_AllPredicate_IsNeverFolded()
    {
        // All(p) is not Where(p).All(): it must see the documents that fail p.
        var all = Model(Expression.Call(typeof(Queryable), "All", [typeof(PersonV2)],
            Q.OrderBy(p => p.Name).Expression, Expression.Quote((Expression<Func<PersonV2, bool>>)(p => p.Age == 3))));
        Assert.True(all.HasComplexOperators);
        Assert.Null(all.WhereClause);
        Assert.Null(all.FoldedTerminal);
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

    [Fact]
    public void Terminals_AfterGroupBy_PredicateIsOverGroups()
    {
        // The predicate takes an IGrouping, not a PersonV2, so it must not reach FetchAsync.
        Assert.Equal(_people.GroupBy(p => p.Age).Count(g => g.Count() > 5), Q.GroupBy(p => p.Age).Count(g => g.Count() > 5));
        Assert.True(Q.GroupBy(p => p.Age).Any(g => g.Key == 3));
        Assert.Equal(3, Q.GroupBy(p => p.Age).First(g => g.Key == 3).Key);
    }

    [Fact]
    public void Terminals_AfterSkipWhile_PredicateIsNotPushedBelowIt()
    {
        // SkipWhile/TakeWhile depend on the order the collection yields its documents in.
        var scan = Q.ToList();
        Assert.Equal(scan.SkipWhile(p => p.Age < 10).FirstOrDefault(p => p.Age == 5)?.Name,
                     Q.SkipWhile(p => p.Age < 10).FirstOrDefault(p => p.Age == 5)?.Name);
        Assert.Equal(scan.SkipWhile(p => p.Age < 10).Count(p => p.Age == 5),
                     Q.SkipWhile(p => p.Age < 10).Count(p => p.Age == 5));
    }

    [Fact]
    public void Terminals_AfterTakeWhile_PredicateIsNotPushedBelowIt()
    {
        var scan = Q.ToList();
        Assert.Equal(scan.TakeWhile(p => p.Age < 10).Count(p => p.Age == 5),
                     Q.TakeWhile(p => p.Age < 10).Count(p => p.Age == 5));
        Assert.Equal(scan.TakeWhile(p => p.Age < 10).Any(p => p.Age == 5),
                     Q.TakeWhile(p => p.Age < 10).Any(p => p.Age == 5));
    }

    [Fact]
    public void Terminals_AfterOfTypeOrCast_PredicateIsOverTheNewElementType()
    {
        Assert.Equal(_people.Count, Q.OfType<object>().Count(o => o != null));
        Assert.True(Q.Cast<object>().Any(o => o is PersonV2));
        Assert.Equal(_people.Count(p => p.Age == 3), Q.OfType<PersonV2>().Count(p => p.Age == 3));
    }

    [Fact]
    public void Model_PredicateIsFoldedOnlyAfterWhereAndOrdering()
    {
        Expression<Func<PersonV2, bool>> pred = p => p.Age == 3;

        var afterOrdering = Model(Expression.Call(typeof(Queryable), "First", [typeof(PersonV2)],
            Q.Where(p => p.Age > 1).OrderBy(p => p.Name).ThenByDescending(p => p.Age).Expression, Expression.Quote(pred)));
        Assert.NotNull(afterOrdering.WhereClause);
        Assert.Equal("First", afterOrdering.ElementTerminal);

        var afterSkipWhile = Model(Expression.Call(typeof(Queryable), "Count", [typeof(PersonV2)],
            Q.SkipWhile(p => p.Age < 10).Expression, Expression.Quote(pred)));
        Assert.True(afterSkipWhile.HasComplexOperators);
        Assert.Null(afterSkipWhile.WhereClause);
        Assert.Null(afterSkipWhile.FoldedTerminal);

        var afterIndexedWhere = Model(Expression.Call(typeof(Queryable), "Any", [typeof(PersonV2)],
            Q.Where((p, i) => i > 10).Expression, Expression.Quote(pred)));
        Assert.True(afterIndexedWhere.HasComplexOperators);
    }

    [Fact]
    public async Task Count_WithVectorSearchPredicate_CountsTheNearestK()
    {
        for (int i = 0; i < 20; i++)
            await _db.VectorItems.InsertAsync(new VectorEntity { Title = $"v{i}", Embedding = [i, 0f, 0f] });

        float[] query = [0f, 0f, 0f];
        Assert.Equal(3, _db.VectorItems.AsQueryable().Count(x => x.Embedding.VectorSearch(query, 3)));
    }

    [Fact]
    public void Terminals_AfterOrderingWithPredicate_MatchLinqToObjects()
    {
        // The predicate is folded into the WHERE and dropped from the replay, but the ordering
        // must still decide which matching document the terminal returns.
        Assert.Equal(_people.OrderBy(p => p.Name).First(p => p.Age == 7).Name,
                     Q.OrderBy(p => p.Name).First(p => p.Age == 7).Name);
        Assert.Equal(_people.OrderBy(p => p.Name).FirstOrDefault(p => p.Age == 7)!.Name,
                     Q.OrderBy(p => p.Name).FirstOrDefault(p => p.Age == 7)!.Name);
        Assert.Null(Q.OrderBy(p => p.Name).FirstOrDefault(p => p.Age == 999));
        Assert.Equal(_people.OrderBy(p => p.Name).Last(p => p.Age == 7).Name,
                     Q.OrderBy(p => p.Name).Last(p => p.Age == 7).Name);
        Assert.Equal(_people.OrderByDescending(p => p.Name).LastOrDefault(p => p.Age == 7)!.Name,
                     Q.OrderByDescending(p => p.Name).LastOrDefault(p => p.Age == 7)!.Name);
        Assert.Null(Q.OrderBy(p => p.Name).LastOrDefault(p => p.Age == 999));
        Assert.Equal("p42", Q.OrderBy(p => p.Name).Single(p => p.Name == "p42").Name);
        Assert.Null(Q.OrderBy(p => p.Name).SingleOrDefault(p => p.Name == "nobody"));
        Assert.Throws<InvalidOperationException>(() => Q.OrderBy(p => p.Name).Single(p => p.Age == 7));
        Assert.True(Q.OrderBy(p => p.Name).Any(p => p.Age == 49));
        Assert.False(Q.OrderBy(p => p.Name).Any(p => p.Age == 50));

        Assert.Equal(_people.OrderBy(p => p.Age).ThenByDescending(p => p.Name).First(p => p.Name.EndsWith("3")).Name,
                     Q.OrderBy(p => p.Age).ThenByDescending(p => p.Name).First(p => p.Name.EndsWith("3")).Name);
        Assert.Equal(_people.Where(p => p.Age > 10).OrderBy(p => p.Age).ThenBy(p => p.Name).Last(p => p.Age < 20).Name,
                     Q.Where(p => p.Age > 10).OrderBy(p => p.Age).ThenBy(p => p.Name).Last(p => p.Age < 20).Name);
        Assert.Equal(_people.OrderBy(p => p.Age).ThenBy(p => p.Name).Count(p => p.Age == 7),
                     Q.OrderBy(p => p.Age).ThenBy(p => p.Name).Count(p => p.Age == 7));
        Assert.Equal(_people.OrderBy(p => p.Age).ThenBy(p => p.Name).LongCount(p => p.Age == 7),
                     Q.OrderBy(p => p.Age).ThenBy(p => p.Name).LongCount(p => p.Age == 7));

        // All(p) is not folded and still sees every document.
        Assert.True(Q.OrderBy(p => p.Name).All(p => p.Age < 50));
        Assert.False(Q.OrderBy(p => p.Name).All(p => p.Age < 49));
    }

    // Titles run against the vector distance, so ordering by Title picks a different neighbour
    // than the order the index returns them in: the 3 nearest to the origin are "j", "i", "h".
    private static readonly float[] Origin = [0f, 0f, 0f];

    private async Task InsertReverseTitledVectorsAsync()
    {
        for (int i = 0; i < 10; i++)
            await _db.VectorItems.InsertAsync(new VectorEntity { Title = ((char)('j' - i)).ToString(), Embedding = [i, 0f, 0f] });
    }

    private IQueryable<VectorEntity> V => _db.VectorItems.AsQueryable();

    [Fact]
    public async Task ElementTerminals_AfterOrdering_WithVectorSearchPredicate_UseTheFoldedResult()
    {
        await InsertReverseTitledVectorsAsync();

        Assert.Equal("h", V.OrderBy(x => x.Title).First(x => x.Embedding.VectorSearch(Origin, 3)).Title);
        Assert.Equal(V.Where(x => x.Embedding.VectorSearch(Origin, 3)).OrderBy(x => x.Title).First().Title,
                     V.OrderBy(x => x.Title).First(x => x.Embedding.VectorSearch(Origin, 3)).Title);
        Assert.Equal(V.Where(x => x.Embedding.VectorSearch(Origin, 3)).OrderByDescending(x => x.Title).FirstOrDefault()!.Title,
                     V.OrderByDescending(x => x.Title).FirstOrDefault(x => x.Embedding.VectorSearch(Origin, 3))!.Title);
        Assert.Equal("j", V.OrderBy(x => x.Title).Last(x => x.Embedding.VectorSearch(Origin, 3)).Title);
        Assert.Equal(V.Where(x => x.Embedding.VectorSearch(Origin, 3)).OrderBy(x => x.Title).Last().Title,
                     V.OrderBy(x => x.Title).Last(x => x.Embedding.VectorSearch(Origin, 3)).Title);
        Assert.Equal(V.Where(x => x.Embedding.VectorSearch(Origin, 3)).OrderByDescending(x => x.Title).LastOrDefault()!.Title,
                     V.OrderByDescending(x => x.Title).LastOrDefault(x => x.Embedding.VectorSearch(Origin, 3))!.Title);
        Assert.Equal("j", V.OrderBy(x => x.Title).Single(x => x.Embedding.VectorSearch(Origin, 1)).Title);
        Assert.Equal("j", V.OrderBy(x => x.Title).SingleOrDefault(x => x.Embedding.VectorSearch(Origin, 1))!.Title);
        Assert.Throws<InvalidOperationException>(() => V.OrderBy(x => x.Title).Single(x => x.Embedding.VectorSearch(Origin, 3)));
        Assert.True(V.OrderBy(x => x.Title).Any(x => x.Embedding.VectorSearch(Origin, 3)));

        // ThenBy is not modelled by the direct pipeline, so these go through the replay as well.
        Assert.Equal(V.Where(x => x.Title != null && x.Embedding.VectorSearch(Origin, 3)).OrderBy(x => x.Title.Length).ThenBy(x => x.Title).First().Title,
                     V.Where(x => x.Title != null).OrderBy(x => x.Title.Length).ThenBy(x => x.Title).First(x => x.Embedding.VectorSearch(Origin, 3)).Title);
        Assert.Equal("h", V.OrderBy(x => x.Title.Length).ThenBy(x => x.Title).First(x => x.Embedding.VectorSearch(Origin, 3)).Title);
        Assert.Equal(3, V.OrderBy(x => x.Title.Length).ThenBy(x => x.Title).Count(x => x.Embedding.VectorSearch(Origin, 3)));
        Assert.Equal(3L, V.OrderBy(x => x.Title.Length).ThenBy(x => x.Title).LongCount(x => x.Embedding.VectorSearch(Origin, 3)));
        Assert.True(V.OrderBy(x => x.Title.Length).ThenByDescending(x => x.Title).Any(x => x.Embedding.VectorSearch(Origin, 3)));
    }

    [Fact]
    public async Task Terminals_WithUnfoldedVectorSearchPredicate_StillThrow()
    {
        await InsertReverseTitledVectorsAsync();

        // All(p), and any predicate after an operator that blocks folding, is evaluated in the
        // replay, where VectorSearch cannot run.
        Assert.Throws<NotSupportedException>(() => V.OrderBy(x => x.Title).All(x => x.Embedding.VectorSearch(Origin, 3)));
        Assert.Throws<NotSupportedException>(() => V.OrderBy(x => x.Title).SkipWhile(x => x.Title == "a").First(x => x.Embedding.VectorSearch(Origin, 3)));
        Assert.Throws<NotSupportedException>(() => V.OrderBy(x => x.Title).Take(5).Count(x => x.Embedding.VectorSearch(Origin, 3)));
        Assert.Throws<NotSupportedException>(() => V.GroupBy(x => x.Title).Count(g => g.First().Embedding.VectorSearch(Origin, 3)));
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
