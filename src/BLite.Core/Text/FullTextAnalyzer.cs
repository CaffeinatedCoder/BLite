using System.Text;

namespace BLite.Core.Text;

/// <summary>
/// Splits text into the terms a full-text index stores and a full-text query looks up.
/// <para>
/// Text is normalized with <see cref="TextNormalizer.Normalize"/> (accents removed, lowercased,
/// every character that is not a letter or digit treated as a separator) and split into terms.
/// A term longer than <see cref="MaxTermBytes"/> bytes of UTF-8 is cut to that length.
/// Documents and queries go through the same steps, so a document matches a query exactly when
/// it contains every query term, whether or not an index evaluates the query.
/// </para>
/// <para>
/// Scripts written without spaces between words, such as Chinese or Japanese, are not segmented:
/// each run of letters is one term.
/// </para>
/// </summary>
internal static class FullTextAnalyzer
{
    /// <summary>Longest term stored, in bytes of UTF-8; keeps index keys small.</summary>
    internal const int MaxTermBytes = 32;

    /// <summary>
    /// Returns each distinct term of <paramref name="text"/> with its number of occurrences, and
    /// the total number of terms in <paramref name="length"/>.
    /// </summary>
    public static Dictionary<string, int> Analyze(string? text, out int length)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        length = 0;
        foreach (var term in Terms(text))
        {
            frequencies.TryGetValue(term, out var count);
            frequencies[term] = count + 1;
            length++;
        }
        return frequencies;
    }

    /// <summary>The distinct terms of a query, in the order they first appear.</summary>
    public static IReadOnlyList<string> QueryTerms(string? query)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var terms = new List<string>();
        foreach (var term in Terms(query))
        {
            if (seen.Add(term))
                terms.Add(term);
        }
        return terms;
    }

    /// <summary>Whether <paramref name="text"/> contains every term of <paramref name="query"/>.</summary>
    public static bool Matches(string? text, string? query)
    {
        var queryTerms = QueryTerms(query);
        if (queryTerms.Count == 0)
            return false;

        var textTerms = new HashSet<string>(Terms(text), StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            if (!textTerms.Contains(term))
                return false;
        }
        return true;
    }

    private static IEnumerable<string> Terms(string? text)
    {
        var normalized = TextNormalizer.Normalize(text);
        if (normalized.Length == 0)
            yield break;

        foreach (var token in normalized.Split(' '))
        {
            if (token.Length > 0)
                yield return Truncate(token);
        }
    }

    private static string Truncate(string term)
    {
        if (Encoding.UTF8.GetByteCount(term) <= MaxTermBytes)
            return term;

        var bytes = Encoding.UTF8.GetBytes(term);
        int cut = MaxTermBytes;
        // Back off to the start of a character: UTF-8 continuation bytes are 10xxxxxx.
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;
        return Encoding.UTF8.GetString(bytes, 0, cut);
    }
}
