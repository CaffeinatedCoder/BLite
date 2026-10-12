namespace BLite.Core;

public static class VectorSearchExtensions
{
    /// <summary>
    /// Performs a similarity search on a vector property.
    /// This method is a marker for the LINQ query provider, which serves it from the property's
    /// HNSW index. It must appear on its own or as a term of a top-level <c>&amp;&amp;</c>.
    /// </summary>
    /// <param name="vector">The vector property of the entity.</param>
    /// <param name="query">The query vector to compare against.</param>
    /// <param name="k">Number of nearest neighbors to return.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">
    /// Always: whether a document is among the k nearest depends on the whole collection, so the
    /// call cannot be evaluated one document at a time.
    /// </exception>
    public static bool VectorSearch(this float[] vector, float[] query, int k)
    {
        throw NotEvaluable();
    }

    /// <summary>
    /// Performs a similarity search on a collection of vector properties.
    /// Used for entities with multiple vectors per document.
    /// </summary>
    /// <exception cref="NotSupportedException">Always; see <see cref="VectorSearch(float[], float[], int)"/>.</exception>
    public static bool VectorSearch(this IEnumerable<float[]> vectors, float[] query, int k)
    {
        throw NotEvaluable();
    }

    private static NotSupportedException NotEvaluable() => new(
        "VectorSearch selects the k nearest neighbours of the whole collection and cannot be evaluated per document. " +
        "Use it on a property with a vector index, either on its own or combined with other conditions using &&.");
}
