using System.Buffers.Binary;
using System.Text;
using BLite.Core.Storage;
using BLite.Core.Text;
using BLite.Core.Transactions;

namespace BLite.Core.Indexing;

/// <summary>
/// Inverted index for full-text search, stored in a single B-Tree.
/// <list type="bullet">
///   <item>Posting, one per distinct term of a document:
///   <c>[0x10][term UTF-8][0x00][location (6)][term frequency u16][document length u32]</c>,
///   pointing at the document. The term frequency and length make BM25 scoring possible from the
///   postings alone; deleting a document recomputes the same keys from its old text.</item>
///   <item>Statistics, a single entry: <c>[0x20][documents u64][terms u64]</c>, the number of
///   indexed documents and their total length, maintained in the same transaction as the
///   postings.</item>
/// </list>
/// The tags cannot start a key that an older BLite (which reads this tree as a plain B-Tree index)
/// writes: those begin with <c>0x00</c>-<c>0x03</c>, so such keys stay outside every range read here,
/// and <see cref="HasForeignKeys"/> detects them so the index is rebuilt on open.
/// Terms come from <see cref="FullTextAnalyzer"/>; a document without any term is not indexed.
/// <para>
/// Every write updates the single statistics entry, so two explicit transactions writing to the
/// same collection concurrently lose one of the updates (BLite has no write-write conflict
/// detection between transactions). Searches stay correct, but scores drift until the index is
/// rebuilt.
/// </para>
/// </summary>
internal sealed class FullTextIndex
{
    /// <summary>
    /// Version of the analyzer and key layout, recorded as the index's <c>KeyFormat</c>. An index
    /// written with another version is rebuilt on open, since deletes rely on reproducing the keys.
    /// </summary>
    internal const byte FormatVersion = 1;

    private const byte PostingTag = 0x10;
    private const byte StatsTag = 0x20;
    private const byte TermEnd = 0x00;
    private const int StatsKeyLength = 17;

    // BM25 parameters (Robertson et al.; the usual defaults, also used by SQLite FTS5 and Lucene).
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly IndexKey StatsMin = IndexKey.FromOwnedArray([StatsTag]);
    private static readonly IndexKey StatsMax = IndexKey.FromOwnedArray([StatsTag + 1]);
    private static readonly IndexKey AfterPostings = IndexKey.FromOwnedArray([PostingTag + 1]);

    private readonly BTreeIndex _tree;

    public FullTextIndex(StorageEngine storage, IndexOptions options, uint rootPageId = 0)
    {
        _tree = new BTreeIndex(storage, options, rootPageId);
    }

    public uint RootPageId => _tree.RootPageId;

    public IReadOnlyList<uint> CollectAllPages() => _tree.CollectAllPages();

    /// <summary>The index pages as <paramref name="transactionId"/> sees them.</summary>
    public IReadOnlyList<uint> CollectAllPages(ulong transactionId) => _tree.CollectAllPages(transactionId);

    /// <summary>
    /// Whether the tree holds a key this index did not write, such as one inserted by an older BLite
    /// that read the tree as a plain B-Tree index. Such an index is also missing the postings of those
    /// writes, so it must be rebuilt. Reads at most the first key and the keys after the postings.
    /// </summary>
    public bool HasForeignKeys()
    {
        foreach (var entry in _tree.Range(IndexKey.MinKey, IndexKey.MaxKey, IndexDirection.Forward, null))
        {
            var first = entry.Key.Data;
            if (first.Length == 0 || (first[0] != PostingTag && first[0] != StatsTag)) return true;
            break;
        }

        // After the postings only the single statistics entry may follow.
        int count = 0;
        foreach (var entry in _tree.Range(AfterPostings, IndexKey.MaxKey, IndexDirection.Forward, null))
        {
            var key = entry.Key.Data;
            if (++count > 1 || key.Length != StatsKeyLength || key[0] != StatsTag) return true;
        }
        return false;
    }

    public void Insert(string? text, DocumentLocation location, ITransaction? transaction)
    {
        var terms = FullTextAnalyzer.Analyze(text, out var length);
        if (length == 0) return;

        var txnId = transaction?.TransactionId;
        foreach (var pair in terms)
            _tree.Insert(PostingKey(pair.Key, location, pair.Value, length), location, txnId);
        AdjustStats(1, length, txnId);
    }

    public void Delete(string? text, DocumentLocation location, ITransaction? transaction)
    {
        var terms = FullTextAnalyzer.Analyze(text, out var length);
        if (length == 0) return;

        var txnId = transaction?.TransactionId;
        bool found = false;
        foreach (var pair in terms)
            found |= _tree.Delete(PostingKey(pair.Key, location, pair.Value, length), location, txnId);

        // A document that was never indexed must not change the statistics.
        if (found)
            AdjustStats(-1, -length, txnId);
    }

    /// <summary>Locations of the documents that contain every term of <paramref name="query"/>.</summary>
    public IReadOnlyList<DocumentLocation> Match(string? query, ITransaction? transaction)
    {
        var terms = FullTextAnalyzer.QueryTerms(query);
        if (terms.Count == 0) return Array.Empty<DocumentLocation>();

        var txnId = transaction?.TransactionId;
        // Start from the rarest term so the candidate list is as short as possible.
        var ordered = terms
            .Select(t => (Term: t, Count: _tree.CountRange(TermMin(t), TermMax(t), txnId)))
            .OrderBy(t => t.Count)
            .Select(t => t.Term)
            .ToList();

        var result = Postings(ordered[0], txnId).Select(p => p.Location).ToList();
        for (int i = 1; i < ordered.Count && result.Count > 0; i++)
        {
            var present = new HashSet<ulong>(Postings(ordered[i], txnId).Select(p => Pack(p.Location)));
            result.RemoveAll(l => !present.Contains(Pack(l)));
        }
        return result;
    }

    /// <summary>
    /// The <paramref name="k"/> documents with the highest BM25 score for <paramref name="query"/>,
    /// best first. With <paramref name="matchAllTerms"/> only documents containing every query term
    /// qualify; otherwise any term is enough and documents with more of them score higher.
    /// </summary>
    public IReadOnlyList<(DocumentLocation Location, double Score)> Search(string? query, int k, bool matchAllTerms, ITransaction? transaction)
    {
        var terms = FullTextAnalyzer.QueryTerms(query);
        if (terms.Count == 0 || k <= 0) return Array.Empty<(DocumentLocation, double)>();

        var txnId = transaction?.TransactionId;

        // Rarest term first: with matchAllTerms its postings bound the candidate set.
        var ordered = terms
            .Select(t => (Term: t, Count: _tree.CountRange(TermMin(t), TermMax(t), txnId)))
            .OrderBy(t => t.Count)
            .ToList();
        if (matchAllTerms && ordered[0].Count == 0) return Array.Empty<(DocumentLocation, double)>();

        var perTerm = new List<(int DocumentFrequency, List<(DocumentLocation Location, int Frequency, int Length)> Postings)>();
        Dictionary<ulong, DocumentLocation>? candidates = null;
        foreach (var (term, count) in ordered)
        {
            if (count == 0) continue;

            // Once the candidates are few, look each one up instead of reading every posting of a
            // much more common term.
            var postings = candidates != null && candidates.Count * 8 < count
                ? candidates.Values.Select(l => TryPosting(term, l, txnId)).Where(p => p.HasValue).Select(p => p!.Value).ToList()
                : Postings(term, txnId).ToList();

            if (matchAllTerms)
            {
                var next = new Dictionary<ulong, DocumentLocation>();
                foreach (var p in postings)
                {
                    var key = Pack(p.Location);
                    if (candidates == null || candidates.ContainsKey(key)) next[key] = p.Location;
                }
                if (next.Count == 0) return Array.Empty<(DocumentLocation, double)>();
                candidates = next;
            }
            perTerm.Add((count, postings));
        }
        if (perTerm.Count == 0) return Array.Empty<(DocumentLocation, double)>();

        var (documents, totalLength) = ReadStats(txnId);
        if (documents <= 0 || totalLength <= 0)
        {
            // No statistics (e.g. a concurrent transaction's update won): estimate them from the
            // postings at hand rather than return nothing while Match still finds the documents.
            var lengths = perTerm.SelectMany(t => t.Postings).GroupBy(p => Pack(p.Location)).Select(g => g.First().Length).ToList();
            documents = Math.Max(1, perTerm.Max(t => t.DocumentFrequency));
            totalLength = Math.Max(1, (long)(lengths.Average() * documents));
        }
        double averageLength = (double)totalLength / documents;

        var scores = new Dictionary<ulong, (DocumentLocation Location, double Score)>();
        foreach (var (df, postings) in perTerm)
        {
            double idf = Math.Log(1 + (documents - df + 0.5) / (df + 0.5));
            foreach (var p in postings)
            {
                var key = Pack(p.Location);
                if (candidates != null && !candidates.ContainsKey(key)) continue;
                double weight = idf * p.Frequency * (K1 + 1)
                              / (p.Frequency + K1 * (1 - B + B * p.Length / averageLength));
                scores[key] = scores.TryGetValue(key, out var s) ? (s.Location, s.Score + weight) : (p.Location, weight);
            }
        }

        return scores.Values
            .OrderByDescending(s => s.Score)
            .ThenBy(s => Pack(s.Location))
            .Take(k)
            .ToList();
    }

    /// <summary>The posting of <paramref name="term"/> for the document at <paramref name="location"/>, if any.</summary>
    private (DocumentLocation Location, int Frequency, int Length)? TryPosting(string term, DocumentLocation location, ulong? txnId)
    {
        int termBytes = Encoding.UTF8.GetByteCount(term);
        int prefixLength = 2 + termBytes + DocumentLocation.SerializedSize;
        var min = new byte[prefixLength];
        min[0] = PostingTag;
        Encoding.UTF8.GetBytes(term, 0, term.Length, min, 1);
        min[1 + termBytes] = TermEnd;
        location.WriteTo(min.AsSpan(2 + termBytes));
        var max = new byte[prefixLength + 6];
        min.CopyTo(max, 0);
        max.AsSpan(prefixLength).Fill(0xFF);

        foreach (var entry in _tree.Range(IndexKey.FromOwnedArray(min), IndexKey.FromOwnedArray(max), IndexDirection.Forward, txnId))
        {
            var key = entry.Key.Data;
            return (entry.Location,
                    BinaryPrimitives.ReadUInt16BigEndian(key.Slice(prefixLength)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(key.Slice(prefixLength + 2)));
        }
        return null;
    }

    private IEnumerable<(DocumentLocation Location, int Frequency, int Length)> Postings(string term, ulong? txnId)
    {
        int offset = 2 + Encoding.UTF8.GetByteCount(term) + DocumentLocation.SerializedSize;
        foreach (var entry in _tree.Range(TermMin(term), TermMax(term), IndexDirection.Forward, txnId))
        {
            var key = entry.Key.Data;
            yield return (entry.Location,
                          BinaryPrimitives.ReadUInt16BigEndian(key.Slice(offset)),
                          (int)BinaryPrimitives.ReadUInt32BigEndian(key.Slice(offset + 2)));
        }
    }

    private (long Documents, long Length) ReadStats(ulong? txnId)
    {
        foreach (var entry in _tree.Range(StatsMin, StatsMax, IndexDirection.Forward, txnId))
        {
            var key = entry.Key.Data;
            if (key.Length != StatsKeyLength) continue; // not written by this index: ignore
            return ((long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(1)),
                    (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(9)));
        }
        return (0, 0);
    }

    private void AdjustStats(long documents, long length, ulong? txnId)
    {
        long currentDocuments = 0, currentLength = 0;
        foreach (var entry in _tree.Range(StatsMin, StatsMax, IndexDirection.Forward, txnId))
        {
            var key = entry.Key.Data;
            if (key.Length != StatsKeyLength) continue; // not written by this index: ignore
            currentDocuments = (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(1));
            currentLength = (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(9));
            _tree.Delete(entry.Key, entry.Location, txnId);
            break;
        }

        long newDocuments = currentDocuments + documents;
        long newLength = currentLength + length;
        if (newDocuments <= 0) return;

        var stats = new byte[StatsKeyLength];
        stats[0] = StatsTag;
        BinaryPrimitives.WriteUInt64BigEndian(stats.AsSpan(1), (ulong)newDocuments);
        BinaryPrimitives.WriteUInt64BigEndian(stats.AsSpan(9), (ulong)Math.Max(0, newLength));
        _tree.Insert(IndexKey.FromOwnedArray(stats), default, txnId);
    }

    private static IndexKey PostingKey(string term, DocumentLocation location, int frequency, int length)
    {
        int termBytes = Encoding.UTF8.GetByteCount(term);
        var key = new byte[2 + termBytes + DocumentLocation.SerializedSize + 2 + 4];
        key[0] = PostingTag;
        Encoding.UTF8.GetBytes(term, 0, term.Length, key, 1);
        key[1 + termBytes] = TermEnd;
        int offset = 2 + termBytes;
        location.WriteTo(key.AsSpan(offset, DocumentLocation.SerializedSize));
        offset += DocumentLocation.SerializedSize;
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(offset), (ushort)Math.Min(frequency, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(offset + 2), (uint)length);
        return IndexKey.FromOwnedArray(key);
    }

    // [0x10][term][0x00] .. [0x10][term][0x01]: every posting of exactly this term. A longer term
    // sharing the prefix has a letter or digit (> 0x01) where these bounds have 0x00 / 0x01.
    private static IndexKey TermMin(string term) => TermBound(term, TermEnd);
    private static IndexKey TermMax(string term) => TermBound(term, TermEnd + 1);

    private static IndexKey TermBound(string term, int last)
    {
        int termBytes = Encoding.UTF8.GetByteCount(term);
        var key = new byte[2 + termBytes];
        key[0] = PostingTag;
        Encoding.UTF8.GetBytes(term, 0, term.Length, key, 1);
        key[1 + termBytes] = (byte)last;
        return IndexKey.FromOwnedArray(key);
    }

    private static ulong Pack(DocumentLocation location) => ((ulong)location.PageId << 16) | location.SlotIndex;
}
