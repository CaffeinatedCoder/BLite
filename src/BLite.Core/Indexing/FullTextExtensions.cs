using BLite.Core.Text;

namespace BLite.Core;

public static class FullTextExtensions
{
    /// <summary>
    /// Whether <paramref name="text"/> contains every term of <paramref name="query"/>. Terms are
    /// compared after removing accents, lowercasing and treating anything that is not a letter or
    /// digit as a separator, so <c>"Café, au lait!"</c> matches the query <c>"cafe LAIT"</c>.
    /// A query without any term matches nothing.
    /// <para>
    /// The LINQ query provider serves this from the property's full-text index when there is one;
    /// otherwise it is evaluated per document with the same rules.
    /// </para>
    /// </summary>
    /// <param name="text">The string property of the entity.</param>
    /// <param name="query">The terms to look for.</param>
    public static bool FullTextMatch(this string? text, string query)
    {
        return FullTextAnalyzer.Matches(text, query);
    }
}
