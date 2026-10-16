using System.Collections;

namespace BLite.Tests.Oracles;

/// <summary>A row of an oracle test known to disagree, the family of defects it belongs to and the issue that tracks it.</summary>
public sealed record Candidate(string Name, string Family, string Issue);

/// <summary>
/// The known candidates of an oracle test. A listed row is not skipped: while it disagrees it fails
/// with its family label, so triage tells a known candidate from a new one; once it agrees it fails
/// with "candidate fixed", so the entry is removed in the fix's PR and the list never goes stale.
/// </summary>
public sealed class Candidates : IEnumerable<Candidate>
{
    private readonly Dictionary<string, Candidate> _entries = new();

    /// <summary>Lists <paramref name="names"/> under one family; <paramref name="issue"/> stays empty until it is filed.</summary>
    public void Add(string family, string issue, params string[] names)
    {
        foreach (var name in names)
            _entries.Add(name, new Candidate(name, family, issue));
    }

    /// <summary>Passes a row that agrees and is not listed; fails every other row.</summary>
    /// <param name="failure">The disagreement, or null when the row agrees with its oracle.</param>
    public void Verify(string name, string? failure)
    {
        var known = _entries.TryGetValue(name, out var c);
        if (failure == null)
        {
            Assert.False(known, $"candidate fixed: remove the entry for '{name}'");
            return;
        }
        Assert.Fail(known ? $"[known candidate: {c!.Family}{(c.Issue.Length > 0 ? $", {c.Issue}" : "")}] {failure}" : failure);
    }

    /// <summary>Fails when an entry names no row of the test, e.g. after a row was renamed.</summary>
    public void AssertAllIn(IEnumerable<string> rows)
    {
        var stale = _entries.Keys.Except(rows).ToList();
        Assert.True(stale.Count == 0, $"candidates that name no row: {string.Join(", ", stale.Select(s => $"'{s}'"))}");
    }

    public IEnumerator<Candidate> GetEnumerator() => _entries.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
