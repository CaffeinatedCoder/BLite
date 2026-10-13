using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Terminals after <c>Select</c>, and after an indexed <c>OrderBy(...).Take(...)</c>,
/// must return what LINQ-to-Objects returns (#181).
/// </summary>
public class SelectTerminalTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TestDbContext _db;
    private readonly List<PersonV2> _people;

    public SelectTerminalTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"blite_select_terminal_{Guid.NewGuid()}.db");
        _db = new TestDbContext(_dbPath);
        _people = Enumerable.Range(0, 300)
            .Select(i => new PersonV2 { Name = $"p{i}", Age = i % 50 })
            .ToList();
        _db.PeopleV2.InsertBulkAsync(_people).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // Every Age value occurs exactly 6 times. Windows over OrderBy(p => p.Age) either select Age only
    // or end on a multiple of 6, so ties at the window edge cannot change the expected result.
    public static IEnumerable<object[]> Queries() => Cases.Keys.Select(k => new object[] { k });

    private static readonly Dictionary<string, Func<IQueryable<PersonV2>, object?>> Cases = new()
    {
        // Select, then a scalar terminal (the issue's repro).
        ["Select.Any"]                 = q => q.Select(p => p.Age).Any(),
        ["Select.Any(pred)"]           = q => q.Select(p => p.Age).Any(a => a == 7),
        ["Select.Any(pred) none"]      = q => q.Select(p => p.Age).Any(a => a == 700),
        ["Select.All(pred)"]           = q => q.Select(p => p.Age).All(a => a < 50),
        ["Select.Count(pred)"]         = q => q.Select(p => p.Age).Count(a => a == 7),
        ["Select.LongCount(pred)"]     = q => q.Select(p => p.Age).LongCount(a => a == 7),
        ["Select.Max"]                 = q => q.Select(p => p.Age).Max(),
        ["Select.Min"]                 = q => q.Select(p => p.Age).Min(),
        ["Select.Sum"]                 = q => q.Select(p => p.Age).Sum(),
        ["Select.Average"]             = q => q.Select(p => p.Age).Average(),
        ["Select.Contains"]            = q => q.Select(p => p.Age).Contains(49),
        ["Select.ElementAt via OrderBy"] = q => q.Select(p => p.Age).OrderBy(a => a).ElementAt(10),
        ["Select.First(pred)"]         = q => q.Select(p => p.Age).First(a => a == 7),
        ["Select.FirstOrDefault none"] = q => q.Select(p => p.Age).FirstOrDefault(a => a == 700),
        ["Select.Single(pred)"]        = q => q.Select(p => p.Name).Single(n => n == "p7"),
        ["Select.Distinct.Count"]      = q => q.Select(p => p.Age).Distinct().Count(),
        ["Where.Select.Any"]           = q => q.Where(p => p.Age == 7).Select(p => p.Name).Any(),
        ["Where.Select.Any none"]      = q => q.Where(p => p.Age == 700).Select(p => p.Name).Any(),
        ["Where.Select.Max"]           = q => q.Where(p => p.Age > 10).Select(p => p.Age).Max(),
        ["Where.Select.Count(pred)"]   = q => q.Where(p => p.Age > 10).Select(p => p.Age).Count(a => a < 20),
        ["Select.Select.Sum"]          = q => q.Select(p => p.Age).Select(a => a * 2).Sum(),
        ["Select(anon).Any"]           = q => q.Select(p => new { p.Age }).Any(x => x.Age == 7),

        // Select after paging, then a scalar terminal.
        ["Take.Select.Count"]          = q => q.Take(10).Select(p => p.Age).Count(),
        ["Skip.Select.LongCount"]      = q => q.Skip(290).Select(p => p.Age).LongCount(),
        ["OrderBy.Select.Take.Count"]  = q => q.OrderBy(p => p.Name).Select(p => p.Age).Take(10).Count(),
        ["OrderBy.Take.Select.Any"]    = q => q.OrderBy(p => p.Name).Take(10).Select(p => p.Age).Any(),

        // OrderBy on the indexed Age, then Take: the index top-N fast path.
        ["OrderByIdx.Take.Select.Sum"]      = q => q.OrderBy(p => p.Age).Take(10).Select(p => p.Age).Sum(),
        ["OrderByIdx.Take.Select.Max"]      = q => q.OrderBy(p => p.Age).Take(10).Select(p => p.Age).Max(),
        ["OrderByIdx.Take.Select.Count"]    = q => q.OrderBy(p => p.Age).Take(10).Select(p => p.Age).Count(),
        ["OrderByIdx.Skip.Take.Select.Sum"] = q => q.OrderBy(p => p.Age).Skip(20).Take(10).Select(p => p.Age).Sum(),
        ["OrderByIdx.Take.Sum(sel)"]        = q => q.OrderBy(p => p.Age).Take(10).Sum(p => p.Age),
        ["OrderByIdx.Take.Max(sel)"]        = q => q.OrderBy(p => p.Age).Take(10).Max(p => p.Age),
        ["OrderByIdx.Take.Any(pred)"]       = q => q.OrderBy(p => p.Age).Take(10).Any(p => p.Age > 5),
        ["OrderByIdx.Take.Count(pred)"]     = q => q.OrderBy(p => p.Age).Take(10).Count(p => p.Age == 0),
        ["OrderByIdx.Take.Count"]           = q => q.OrderBy(p => p.Age).Take(10).Count(),
        ["OrderByIdx.Take.Last.Age"]        = q => q.OrderBy(p => p.Age).Take(10).Select(p => p.Age).Last(),
        ["OrderByDescIdx.Take.Select.Min"]  = q => q.OrderByDescending(p => p.Age).Take(10).Select(p => p.Age).Min(),
        ["OrderByIdx.Skip.Take.First.Age"]  = q => q.OrderBy(p => p.Age).Skip(20).Take(10).Select(p => p.Age).First(),

        // Window shapes the index top-N shortcut must not take, or must replay.
        ["OrderByIdx.Take.Skip.List"]       = q => string.Join(",", q.OrderBy(p => p.Age).Take(10).Skip(5).Select(p => p.Age).ToList()),
        ["OrderByIdx.Skip.Take.Skip.Take"]  = q => string.Join(",", q.OrderBy(p => p.Age).Skip(10).Take(20).Skip(5).Take(3).Select(p => p.Age).ToList()),
        ["OrderByIdx.Skip.Take.Select.Skip"] = q => string.Join(",", q.OrderBy(p => p.Age).Skip(5).Take(10).Select(p => p.Age).Skip(2).ToList()),
        ["OrderByIdx.Take.Select.Take"]     = q => string.Join(",", q.OrderBy(p => p.Age).Take(10).Select(p => p.Age).Take(8).ToList()),
        ["OrderByIdx.ThenBy.Take.List"]     = q => string.Join(",", q.OrderBy(p => p.Age).ThenBy(p => p.Name).Take(7).Select(p => p.Name).ToList()),
        ["OrderByIdx.Take.ThenBy.List"]     = q => string.Join(",", q.OrderBy(p => p.Age).Take(12).OrderBy(p => p.Age).ThenByDescending(p => p.Name).Select(p => p.Name).ToList()),
        ["Select(anon).OrderBy.Take.List"]  = q => string.Join(",", q.Select(p => new { p.Age, p.Name }).OrderBy(x => x.Age).Take(3).Select(x => x.Age).ToList()),
        ["OrderByIdx.Skip.Take.First"]      = q => q.OrderBy(p => p.Age).Skip(3).Take(10).First().Age,
        ["OrderByIdx.Take(0).Any"]          = q => q.OrderBy(p => p.Age).Take(0).Select(p => p.Age).Any(),

        // The root queryable used a second time after the window: that use reads the whole
        // collection, not the window prefix the replay is given.
        ["OrderByIdx.Take.Concat(root).Count"]        = q => q.OrderBy(p => p.Age).Take(5).Concat(q).Count(),
        ["OrderByIdx.Take.Select.Concat(root).Count"] = q => q.OrderBy(p => p.Age).Take(5).Select(p => p.Age).Concat(q.Select(p => p.Age)).Count(),
        ["OrderByIdx.Take.Select.Union(root).Count"]  = q => q.OrderBy(p => p.Age).Take(5).Select(p => p.Age).Union(q.Select(p => p.Age)).Count(),
        ["OrderByIdx.Take.Join(root).Count"]          = q => q.OrderBy(p => p.Age).Take(5).Join(q, a => a.Age, b => b.Age, (a, b) => b.Name).Count(),

        // A lambda that captures the queryable reads it through a closure field, not a constant,
        // so the replay does not replace it with the prefix: it runs as its own query.
        ["OrderByIdx.Take.Count(closure root)"]       = q => q.OrderBy(p => p.Age).Take(12).Count(x => q.Any(y => y.Age == 49)),
        ["OrderByIdx.Take.Count(correlated closure)"] = q => q.OrderBy(p => p.Age).Take(12).Count(x => q.Any(y => y.Age == x.Age + 1)),
        ["Count(correlated closure)"]                 = q => q.Count(x => q.Any(y => y.Age == x.Age + 1)),

        // Parameterless Sum/Average after Select (non-generic Queryable overloads).
        ["Select(long).Sum"]           = q => q.Select(p => (long)p.Age).Sum(),
        ["Select(double).Average"]     = q => q.Select(p => (double)p.Age).Average(),
        ["Select(decimal?).Sum"]       = q => q.Select(p => (decimal?)p.Age).Sum(),

        // Shapes that already worked, kept as guards.
        ["Select.Count"]               = q => q.Select(p => p.Age).Count(),
        ["Select.ToList.Count"]        = q => q.Select(p => p.Age).ToList().Count,
        ["Where.Select.ToList"]        = q => string.Join(",", q.Where(p => p.Age == 7).Select(p => p.Name).ToList().OrderBy(n => n)),
        ["OrderByIdx.Take.Select.List"]= q => string.Join(",", q.OrderBy(p => p.Age).Take(12).Select(p => p.Age).ToList()),
        ["OrderByIdx.Skip.Take.List"]  = q => string.Join(",", q.OrderBy(p => p.Age).Skip(20).Take(10).Select(p => p.Age).ToList()),
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void Terminal_MatchesLinqToObjects(string name)
    {
        var query = Cases[name];
        var expected = query(_people.AsQueryable());
        var actual = query(_db.PeopleV2.AsQueryable());
        Assert.Equal(expected, actual);
    }
}
