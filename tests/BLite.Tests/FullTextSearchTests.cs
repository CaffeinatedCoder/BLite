using BLite.Bson;
using BLite.Core;
using System.Linq.Expressions;
using BLite.Core.Indexing;
using BLite.Core.Query;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Full-text indexes: analysis, LINQ <c>FullTextMatch</c>, BM25-ranked search, index maintenance
/// and the dynamic API (#70).
/// </summary>
public class FullTextSearchTests : IDisposable
{
    private readonly string _dbPath;

    public FullTextSearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"fts_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".fresh.db") })
            foreach (var f in new[] { path, Path.ChangeExtension(path, ".wal") })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private static Article A(string title, string? body, int year = 2020) => new() { Title = title, Body = body, Year = year };

    private static async Task SeedAsync(FullTextDbContext db)
    {
        await db.Articles.InsertAsync(A("fox", "The quick brown fox jumps over the lazy dog", 2001));
        await db.Articles.InsertAsync(A("fox again", "A quick fox. Another quick fox!", 1999));
        await db.Articles.InsertAsync(A("dog", "The dog sleeps all day long in the sun", 2010));
        await db.Articles.InsertAsync(A("cafe", "Café au lait, s'il vous plaît", 2015));
        await db.Articles.InsertAsync(A("empty", null, 2020));
    }

    private static List<string> Titles(IEnumerable<Article> articles) => articles.Select(a => a.Title).OrderBy(t => t).ToList();

    // ── In-memory matching ──────────────────────────────────────────────────

    [Fact]
    public void FullTextMatch_InMemory_IgnoresCaseAccentsAndPunctuation()
    {
        Assert.True("Café, au LAIT!".FullTextMatch("cafe lait"));
        Assert.True("The quick brown fox".FullTextMatch("FOX quick"));
        Assert.False("The quick brown fox".FullTextMatch("quick dog"));
        Assert.False("The quick brown fox".FullTextMatch("  ,;  "));
        Assert.False(((string?)null).FullTextMatch("fox"));
    }

    // ── LINQ ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Linq_FullTextMatch_ReturnsDocumentsContainingEveryTerm()
    {
        using var db = new FullTextDbContext(_dbPath);
        await SeedAsync(db);

        Assert.Equal(new[] { "fox", "fox again" }, Titles(db.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("quick fox")).ToList()));
        Assert.Equal(new[] { "fox" }, Titles(db.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("fox dog")).ToList()));
        Assert.Equal(new[] { "cafe" }, Titles(db.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("CAFE")).ToList()));
        Assert.Empty(db.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("cat")).ToList());
    }

    [Fact]
    public async Task Linq_FullTextMatch_CombinesWithOtherConditions()
    {
        using var db = new FullTextDbContext(_dbPath);
        await SeedAsync(db);

        var and = db.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("fox") && a.Year > 2000).ToList();
        var or = db.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("sun") || a.Year == 1999).ToList();
        var not = db.Articles.AsQueryable().Where(a => a.Body != null && !a.Body.FullTextMatch("fox")).ToList();

        Assert.Equal(new[] { "fox" }, Titles(and));
        Assert.Equal(new[] { "dog", "fox again" }, Titles(or));
        Assert.Equal(new[] { "cafe", "dog" }, Titles(not));
    }

    [Fact]
    public async Task Linq_CountAndFirst_UseTheFullTextIndex()
    {
        using var db = new FullTextDbContext(_dbPath);
        await SeedAsync(db);

        Assert.Equal(2, db.Articles.AsQueryable().Count(a => a.Body.FullTextMatch("quick")));
        Assert.Equal("dog", db.Articles.AsQueryable().First(a => a.Body.FullTextMatch("sleeps")).Title);
        Assert.Null(db.Articles.AsQueryable().FirstOrDefault(a => a.Body.FullTextMatch("cat")));
    }

    [Fact]
    public async Task Linq_EqualityOnAPropertyWithBothIndexes_UsesTheBTree()
    {
        using var db = new FullTextDbContext(_dbPath);
        await SeedAsync(db);

        Assert.Equal("fox again", db.Articles.AsQueryable().Single(a => a.Title == "fox again").Title);
        Assert.Equal(2, db.Articles.AsQueryable().Count(a => a.Title == "fox" || a.Title == "dog"));
        Assert.Equal(new[] { "fox", "fox again" }, Titles(db.Articles.AsQueryable().Where(a => a.Title.FullTextMatch("fox")).ToList()));
    }

    // ── Query planning ──────────────────────────────────────────────────────

    private static readonly CollectionIndexInfo[] PlannerIndexes =
    [
        new() { Name = "idx_fts_Title", PropertyPaths = ["Title"], Type = IndexType.FullText },
        new() { Name = "idx_Title", PropertyPaths = ["Title"], Type = IndexType.BTree },
        new() { Name = "idx_fts_Body", PropertyPaths = ["Body"], Type = IndexType.FullText },
    ];

    private static IndexOptimizer.OptimizationResult? Plan(Expression<Func<Article, bool>> predicate)
        => IndexOptimizer.TryOptimize<Article>(new QueryModel { WhereClause = predicate }, PlannerIndexes);

    [Fact]
    public void Planner_ServesFullTextMatch_FromTheFullTextIndex()
    {
        var plan = Plan(a => a.Body.FullTextMatch("quick fox"));

        Assert.NotNull(plan);
        Assert.True(plan.IsFullTextSearch);
        Assert.Equal("idx_fts_Body", plan.IndexName);
        Assert.Equal("quick fox", plan.FullTextQuery);
        Assert.Equal(IndexOptimizer.FilterCompleteness.Exact, plan.FilterCompleteness);
    }

    [Fact]
    public void Planner_KeyLookups_SkipFullTextIndexes()
    {
        var plan = Plan(a => a.Title == "fox");

        Assert.NotNull(plan);
        Assert.False(plan.IsFullTextSearch);
        Assert.Equal("idx_Title", plan.IndexName);
    }

    [Fact]
    public void Planner_FullTextMatchWithAnotherCondition_PostFilters()
    {
        var plan = Plan(a => a.Body.FullTextMatch("fox") && a.Year > 2000);

        Assert.NotNull(plan);
        Assert.True(plan.IsFullTextSearch);
        Assert.Equal(IndexOptimizer.FilterCompleteness.PartialAnd, plan.FilterCompleteness);
    }

    [Fact]
    public void Planner_FullTextMatchWithoutAnIndex_HasNoPlan()
    {
        var indexes = new[] { new CollectionIndexInfo { Name = "idx_Title", PropertyPaths = ["Title"], Type = IndexType.BTree } };

        Assert.Null(IndexOptimizer.TryOptimize<Article>(
            new QueryModel { WhereClause = (Expression<Func<Article, bool>>)(a => a.Body.FullTextMatch("fox")) }, indexes));
    }

    // ── Ranked search ───────────────────────────────────────────────────────

    [Fact]
    public async Task FullTextSearch_RanksByBm25()
    {
        using var db = new FullTextDbContext(_dbPath);
        await db.Articles.InsertAsync(A("once, long", "fox " + string.Join(" ", Enumerable.Repeat("filler", 30))));
        await db.Articles.InsertAsync(A("twice, short", "fox fox"));
        await db.Articles.InsertAsync(A("none", "nothing to see here"));

        var results = await db.Articles.FullTextSearchAsync("idx_fts_Body", "fox", 10).ToListAsync();

        Assert.Equal(new[] { "twice, short", "once, long" }, results.Select(r => r.Document.Title));
        Assert.True(results[0].Score > results[1].Score);
        Assert.All(results, r => Assert.True(r.Score > 0));
    }

    [Fact]
    public async Task FullTextSearch_AnyTerm_RanksDocumentsWithMoreAndRarerTermsFirst()
    {
        // Every body has two terms, so only term rarity and the number of matching terms differ.
        using var db = new FullTextDbContext(_dbPath);
        await db.Articles.InsertAsync(A("common", "apple filler"));
        await db.Articles.InsertAsync(A("common 2", "apple filler"));
        await db.Articles.InsertAsync(A("common 3", "apple filler"));
        await db.Articles.InsertAsync(A("rare", "durian filler"));
        await db.Articles.InsertAsync(A("both", "apple durian"));

        var all = await db.Articles.FullTextSearchAsync("idx_fts_Body", "apple durian", 10).ToListAsync();
        var any = await db.Articles.FullTextSearchAsync("idx_fts_Body", "apple durian", 10, matchAllTerms: false).ToListAsync();
        var top2 = await db.Articles.FullTextSearchAsync("idx_fts_Body", "apple durian", 2, matchAllTerms: false).ToListAsync();

        Assert.Equal(new[] { "both" }, all.Select(r => r.Document.Title));
        Assert.Equal(5, any.Count);
        Assert.Equal(new[] { "both", "rare" }, any.Take(2).Select(r => r.Document.Title));
        Assert.Equal(new[] { "both", "rare" }, top2.Select(r => r.Document.Title));
    }

    [Fact]
    public async Task FullTextSearch_ScoresAfterUpdatesAndDeletes_MatchAFreshIndex()
    {
        using (var db = new FullTextDbContext(_dbPath))
        {
            var keep = A("keep", "red green blue");
            var change = A("change", "red red");
            var gone = A("gone", "green green green blue");
            await db.Articles.InsertAsync(keep);
            await db.Articles.InsertAsync(change);
            await db.Articles.InsertAsync(gone);
            await db.Articles.InsertAsync(A("gone too", "blue"));
            change.Body = "blue green with many more words here";
            await db.Articles.UpdateAsync(change);
            await db.Articles.DeleteAsync(gone.Id);
            await db.Articles.DeleteAsync(db.Articles.AsQueryable().Single(a => a.Title == "gone too").Id);
        }

        var freshPath = Path.ChangeExtension(_dbPath, ".fresh.db");
        using (var fresh = new FullTextDbContext(freshPath))
        {
            await fresh.Articles.InsertAsync(A("keep", "red green blue"));
            await fresh.Articles.InsertAsync(A("change", "blue green with many more words here"));
        }

        foreach (var query in new[] { "blue", "green", "red", "blue green" })
        {
            using var db = new FullTextDbContext(_dbPath);
            using var fresh = new FullTextDbContext(freshPath);
            var actual = await db.Articles.FullTextSearchAsync("idx_fts_Body", query, 10, matchAllTerms: false).ToListAsync();
            var expected = await fresh.Articles.FullTextSearchAsync("idx_fts_Body", query, 10, matchAllTerms: false).ToListAsync();

            Assert.Equal(expected.Select(r => r.Document.Title), actual.Select(r => r.Document.Title));
            Assert.Equal(expected.Select(r => Math.Round(r.Score, 9)), actual.Select(r => Math.Round(r.Score, 9)));
        }
    }

    // ── Maintenance ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatesAndDeletes_AreReflected()
    {
        using var db = new FullTextDbContext(_dbPath);
        var a = A("a", "alpha beta");
        var b = A("b", "gamma");
        await db.Articles.InsertAsync(a);
        await db.Articles.InsertAsync(b);

        a.Body = "delta";
        await db.Articles.UpdateAsync(a);
        b.Body = "epsilon " + new string('x', 3000); // no longer fits its slot
        await db.Articles.UpdateAsync(b);
        await db.Articles.InsertAsync(A("c", "zeta"));
        await db.Articles.DeleteAsync(db.Articles.AsQueryable().Single(x => x.Title == "c").Id);
        await db.Articles.InsertAsync(A("d", "unrelated words")); // may reuse the deleted slot

        List<string> Match(string q) => Titles(db.Articles.AsQueryable().Where(x => x.Body.FullTextMatch(q)).ToList());
        Assert.Empty(Match("alpha"));
        Assert.Equal(new[] { "a" }, Match("delta"));
        Assert.Empty(Match("gamma"));
        Assert.Equal(new[] { "b" }, Match("epsilon"));
        Assert.Empty(Match("zeta"));
        Assert.Empty(await db.Articles.FullTextSearchAsync("idx_fts_Body", "zeta", 10).ToListAsync());
    }

    [Fact]
    public async Task RolledBackWrites_LeaveTheIndexUnchanged()
    {
        using var db = new FullTextDbContext(_dbPath);
        var kept = A("kept", "persistent text");
        await db.Articles.InsertAsync(kept);

        using (var txn = await db.BeginTransactionAsync())
        {
            await db.Articles.InsertAsync(A("rolled back", "ephemeral text"), txn);
            await db.Articles.DeleteAsync(kept.Id, txn);
            await txn.RollbackAsync();
        }

        Assert.Equal(new[] { "kept" }, Titles(db.Articles.AsQueryable().Where(x => x.Body.FullTextMatch("text")).ToList()));
        var results = await db.Articles.FullTextSearchAsync("idx_fts_Body", "text", 10).ToListAsync();
        Assert.Equal("kept", Assert.Single(results).Document.Title);
    }

    [Fact]
    public async Task Index_SurvivesReopen()
    {
        double score;
        using (var db = new FullTextDbContext(_dbPath))
        {
            await SeedAsync(db);
            score = (await db.Articles.FullTextSearchAsync("idx_fts_Body", "dog", 10).ToListAsync())[0].Score;
        }

        using var reopened = new FullTextDbContext(_dbPath);
        var results = await reopened.Articles.FullTextSearchAsync("idx_fts_Body", "dog", 10).ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(score, results[0].Score, 9);
        Assert.Contains(reopened.Articles.GetIndexes(), i => i.Name == "idx_fts_Body" && i.Type == IndexType.FullText);
    }

    [Fact]
    public async Task IndexAddedToExistingData_CoversIt()
    {
        using (var v1 = new FullTextNoIndexDbContext(_dbPath))
        {
            await v1.Articles.InsertAsync(A("old", "written before the index existed"));
        }

        using (var v1 = new FullTextNoIndexDbContext(_dbPath))
        {
            await v1.Articles.CreateFullTextIndexAsync(a => a.Title, "idx_title_runtime");
            await v1.Articles.CreateFullTextIndexAsync(a => a.Title, "idx_title_runtime"); // existing: no-op
            Assert.Single(await v1.Articles.FullTextSearchAsync("idx_title_runtime", "old", 10).ToListAsync());
        }

        using var v2 = new FullTextDbContext(_dbPath);
        Assert.Equal(new[] { "old" }, Titles(v2.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("index existed")).ToList()));
    }

    [Fact]
    public async Task VacuumRebuild_KeepsResultsAndScores()
    {
        using var db = new FullTextDbContext(_dbPath);
        await SeedAsync(db);
        var before = await db.Articles.FullTextSearchAsync("idx_fts_Body", "quick fox", 10).ToListAsync();

        await db.Articles.VacuumAsync(new VacuumOptions { RebuildIndexes = true });
        await db.Articles.VacuumAsync(new VacuumOptions { RebuildIndexes = true });
        var after = await db.Articles.FullTextSearchAsync("idx_fts_Body", "quick fox", 10).ToListAsync();

        Assert.Equal(before.Select(r => r.Document.Title), after.Select(r => r.Document.Title));
        Assert.Equal(before.Select(r => Math.Round(r.Score, 9)), after.Select(r => Math.Round(r.Score, 9)));
    }

    [Fact]
    public async Task DroppedIndex_FallsBackToScanning()
    {
        using var db = new FullTextDbContext(_dbPath);
        await SeedAsync(db);

        Assert.True(await db.Articles.DropIndexAsync("idx_fts_Body"));

        Assert.Equal(new[] { "fox", "fox again" }, Titles(db.Articles.AsQueryable().Where(a => a.Body.FullTextMatch("quick fox")).ToList()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await db.Articles.FullTextSearchAsync("idx_fts_Body", "fox", 1).ToListAsync());
    }

    // ── DynamicCollection ───────────────────────────────────────────────────

    [Fact]
    public async Task Dynamic_CreateSearchUpdateDeleteAndReopen()
    {
        BsonId movedId;
        using (var engine = new BLiteEngine(_dbPath))
        {
            var col = engine.GetOrCreateCollection("notes");
            BsonDocument Doc(string title, string text) => col.CreateDocument(["title", "text"], b => b
                .AddString("title", title).AddString("text", text));

            await col.InsertAsync(Doc("before index", "indexed later on"));
            await col.CreateFullTextIndexAsync("text");
            movedId = await col.InsertAsync(Doc("moved", "original words"));
            var goneId = await col.InsertAsync(Doc("gone", "words to forget"));

            await col.UpdateAsync(movedId, Doc("moved", "replacement words"));
            await col.DeleteAsync(goneId);

            Assert.Contains(col.GetIndexDescriptors(), d => d.Name == "idx_fts_text" && d.IsFullText);
        }

        using (var engine = new BLiteEngine(_dbPath))
        {
            var col = engine.GetOrCreateCollection("notes");
            async Task<List<string?>> Search(string q) =>
                (await col.FullTextSearchAsync("idx_fts_text", q, 10).ToListAsync())
                    .Select(r => r.Document.TryGetString("title", out var t) ? t : null).ToList();

            Assert.Equal(new[] { "before index" }, await Search("later"));
            Assert.Equal(new[] { "moved" }, await Search("words"));
            Assert.Empty(await Search("original"));
            Assert.Empty(await Search("forget"));
        }
    }
}
