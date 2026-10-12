namespace BLite.Core;

/// <summary>A document returned by a ranked full-text search, with its BM25 relevance score.</summary>
/// <param name="Document">The matching document.</param>
/// <param name="Score">The BM25 score; higher is more relevant. Scores compare only within one search.</param>
public readonly record struct FullTextSearchResult<T>(T Document, double Score);
