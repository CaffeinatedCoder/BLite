using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BLite.Bson;
using BLite.Core.Audit;
using BLite.Core.Collections;
using BLite.Core.Indexing;
using BLite.Core.Metadata;
using BLite.Core.Metrics;
using static BLite.Core.Query.IndexOptimizer;

namespace BLite.Core.Query;

public class BTreeQueryProvider<TId, T> : IQueryProvider, IAsyncQueryProvider, IBTreeQueryCore<T> where T : class
{
    private readonly DocumentCollection<TId, T> _collection;
    private readonly ValueConverterRegistry _converterRegistry;

    // ── Reflection cache (computed once per TId+T combination) ────────────────
    // BsonProjectionCompiler.TryCompile<T, TResult> generic method definition.
    // Targets the 3-parameter overload (selectLambda, whereLambda, keyMap) so that
    // the key map can be threaded in for offset-table fast paths.
#pragma warning disable IL2026, IL2075
    private static readonly MethodInfo s_tryCompileMethod =
        typeof(BsonProjectionCompiler)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(m => m.Name == nameof(BsonProjectionCompiler.TryCompile)
                         && m.GetParameters().Length == 3);

    // DocumentCollection<TId,T>.ScanAsync<TResult>(BsonReaderProjector<TResult>, CancellationToken) — generic overload.
    private static readonly MethodInfo s_scanProjectorMethod =
        typeof(DocumentCollection<TId, T>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "ScanAsync" && m.IsGenericMethodDefinition);
#pragma warning restore IL2026, IL2075

    // ── Per-projection-type MakeGenericMethod cache (Fase 3) ─────────────────
    // Keyed on TProj. Avoids calling MakeGenericMethod on every query execution.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo>
        s_compiledSelectMethods = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo>
        s_compiledScanMethods = new();

    public BTreeQueryProvider(DocumentCollection<TId, T> collection, ValueConverterRegistry? registry = null)
    {
        _collection = collection;
        _converterRegistry = registry ?? ValueConverterRegistry.Empty;
    }

    // NOTE: CreateQuery implements IQueryProvider.CreateQuery which cannot be annotated.
    // The body uses dynamic code (Compile, MakeGenericMethod) suppressed via pragma.
#pragma warning disable IL3050, IL2026, IL2075
    public IQueryable CreateQuery(Expression expression)
    {
        // IQueryProvider.CreateQuery(Expression) is called only for operators that change the element
        // type at runtime (e.g. GroupBy, Cast).  For the common case (same T) create directly.
        var elementType = expression.Type.IsGenericType
            ? expression.Type.GetGenericArguments()[0]
            : typeof(T);

        if (elementType == typeof(T))
            return new BTreeQueryable<T>(this, expression);

        // Rare path: element type differs from T.
        // Cache a compiled delegate Func<BTreeQueryProvider<TId,T>, Expression, IQueryable>.
        // Using a 2-param delegate (provider, expr) avoids capturing 'this' in the static cache
        // — which would be a bug: the cached lambda would call the FIRST provider instance forever.
        var del = s_createQueryDelegates.GetOrAdd(elementType, t =>
        {
            var method = typeof(BTreeQueryProvider<TId, T>)
                .GetMethod(nameof(CreateQueryTyped), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(t);
            var providerParam = Expression.Parameter(typeof(BTreeQueryProvider<TId, T>), "p");
            var exprParam = Expression.Parameter(typeof(Expression), "e");
            var callExpr = Expression.Call(providerParam, method, exprParam);
            var castResult = Expression.Convert(callExpr, typeof(IQueryable));
            return Expression.Lambda<Func<BTreeQueryProvider<TId, T>, Expression, IQueryable>>(
                castResult, providerParam, exprParam).Compile();
        });
        return del(this, expression);
    }
#pragma warning restore IL3050, IL2026, IL2075

    private BTreeQueryable<TElement> CreateQueryTyped<TElement>(Expression expression)
        => new(this, expression);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Type, Func<BTreeQueryProvider<TId, T>, Expression, IQueryable>>
        s_createQueryDelegates = new();

    // Explicit implementation satisfies IQueryProvider contract.
    // The runtime object is always BTreeQueryable<TElement> : IBLiteQueryable<TElement>,
    // so callers can safely use AsAsyncEnumerable() or the typed LINQ extensions.
    IQueryable<TElement> IQueryProvider.CreateQuery<TElement>(Expression expression)
        => new BTreeQueryable<TElement>(this, expression);

    /// <summary>Typed overload — returns the richer <see cref="IBLiteQueryable{TElement}"/> interface.</summary>
    public IBLiteQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new BTreeQueryable<TElement>(this, expression);

    // NOTE: Execute/Execute<TResult> implement IQueryProvider methods which cannot be annotated.
    // Use pragma to suppress warnings about calling dynamic code methods within the body.
#pragma warning disable IL3050, IL2026
    public object? Execute(Expression expression)
    {
        return Execute<object>(expression);
    }

    public TResult Execute<TResult>(Expression expression)
    {
        // Run on the thread pool to avoid deadlocks when called from a thread that
        // has a SynchronizationContext (ASP.NET classic, WPF, Blazor).
        // Without Task.Run, ExecuteAsync awaits FindAllAsync which uses SemaphoreSlim.WaitAsync;
        // its continuation would try to resume on the captured context that is already blocked
        // by this .GetAwaiter().GetResult() call → deadlock.
        // BTreeQueryable.GetAsyncEnumerator applies the same pattern for the async path.
        return Task.Run(() => ExecuteAsync<TResult>(expression, CancellationToken.None))
                   .GetAwaiter().GetResult();
    }
#pragma warning restore IL3050, IL2026

    // Explicit IAsyncQueryProvider implementation — delegates to the private async path,
    // allowing BTreeQueryable<T> to call ExecuteAsync directly (no double Task.Run).
    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    [RequiresUnreferencedCode("BLite LINQ queries use reflection to resolve methods and types at runtime. Ensure all entity types and their members are preserved.")]
    Task<TResult> IAsyncQueryProvider.ExecuteAsync<TResult>(Expression expression, CancellationToken ct)
        => ExecuteAsync<TResult>(expression, ct);

    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    [RequiresUnreferencedCode("BLite LINQ queries use reflection to resolve methods and types at runtime. Ensure all entity types and their members are preserved.")]
    private async Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken)
    {
        // ── AUDIT: start ─────────────────────────────────────────────────────
        var auditOpts = _collection.Storage.AuditOptions;
        var auditVsw  = (auditOpts is not null && (auditOpts.Sink is not null || auditOpts.EnableMetrics))
            ? ValueStopwatch.StartNew()
            : default;

#if NET5_0_OR_GREATER
        Activity? activity = auditOpts?.EnableDiagnosticSource == true
            ? BLiteDiagnostics.ActivitySource.StartActivity(BLiteDiagnostics.QueryActivityName)
            : null;
        activity?.SetTag("db.system",           "blite");
        activity?.SetTag("db.collection.name",  _collection.CollectionName);
#endif
        // ────────────────────────────────────────────────────────────────────

        try
        {
            var result = await ExecuteAsyncCore<TResult>(expression, cancellationToken).ConfigureAwait(false);

            // ── AUDIT: emit on success ────────────────────────────────────────
            if (auditVsw.IsActive)
            {
                var elapsed = auditVsw.GetElapsed();
                var opts    = _collection.Storage.AuditOptions!;
                var userId  = (opts.ContextProvider ?? AmbientAuditContext.Instance).GetCurrentUserId();

                var evt = new QueryAuditEvent(
                    CollectionName: _collection.CollectionName,
                    // Phase 1: strategy resolution requires BTreeQueryVisitor integration
                    // (index vs full-scan detection). Deferred to a future release.
                    Strategy:       QueryStrategy.Unknown,
                    IndexName:      null,
                    ResultCount:    -1,
                    Elapsed:        elapsed,
                    UserId:         userId);

                _collection.Storage.AuditSink?.OnQuery(evt);
                _collection.Storage.AuditMetrics?.RecordQuery(QueryStrategy.Unknown, elapsed);

                // Slow-query detection
                if (opts.SlowOperationThreshold is { } threshold && elapsed > threshold)
                {
                    _collection.Storage.AuditSink?.OnSlowOperation(new SlowOperationEvent(
                        SlowOperationType.Query,
                        CollectionName: _collection.CollectionName,
                        Elapsed:        elapsed,
                        Detail:         null));
                }
            }

#if NET5_0_OR_GREATER
            // Always dispose the activity (even when metrics/sink not active) so ActivityStopped fires.
            activity?.SetTag("db.blite.query_strategy", QueryStrategy.Unknown.ToString());
            activity?.Dispose();
            activity = null;
#endif
            // ────────────────────────────────────────────────────────────────

            return result;
        }
        catch
        {
#if NET5_0_OR_GREATER
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.Dispose();
#endif
            throw;
        }
    }

    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    [RequiresUnreferencedCode("BLite LINQ queries use reflection to resolve methods and types at runtime. Ensure all entity types and their members are preserved.")]
    private async Task<TResult> ExecuteAsyncCore<TResult>(Expression expression, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Parse the LINQ expression tree into a flat QueryModel.
        var visitor = new BTreeExpressionVisitor();
        visitor.Visit(expression);
        var model = visitor.GetModel();

        // ── Fast path: Count() / LongCount() terminal with no WHERE ─────────────
        // Uses the primary BTree key scan (O(n) key reads, zero document reads).
        // Disabled when Take/Skip are present — those shaping operators reduce the
        // logical row-set before counting and must not be bypassed.
        if (model.IsCountOnly && model.WhereClause == null && !model.HasComplexOperators
            && !model.Take.HasValue && !model.Skip.HasValue)
        {
            var count = await _collection.CountAsync(cancellationToken);
            if (typeof(TResult) == typeof(int))    return (TResult)(object)count;
            if (typeof(TResult) == typeof(long))   return (TResult)(object)(long)count;
            if (typeof(TResult) == typeof(object)) return (TResult)(object)count;
        }

        // ── Fast path: Count() / LongCount() terminal with WHERE predicate ────────
        // Uses index key-only scan for indexed predicates (zero document reads) or a
        // streaming count for non-indexed predicates (no large List<T> accumulation).
        // Disabled when Take/Skip are present for the same reason as above.
        if (model.IsCountOnly && model.WhereClause != null && !model.HasComplexOperators
            && !model.Take.HasValue && !model.Skip.HasValue)
        {
            var count = await _collection.CountByPredicateAsync(model.WhereClause, cancellationToken);
            if (typeof(TResult) == typeof(int))    return (TResult)(object)count;
            if (typeof(TResult) == typeof(long))   return (TResult)(object)(long)count;
            if (typeof(TResult) == typeof(object)) return (TResult)(object)count;
        }

        // ── Fast path: Sum / Average / Min / Max via BSON field-projection scan ─
        // Reads only the target field from raw BSON — T is never fully instantiated.
        // Fires for both no-WHERE and WHERE variants:
        //   - No WHERE:  projector reads only the selector field.
        //   - With WHERE: BsonProjectionCompiler merges WHERE + SELECT fields into one
        //     BSON pass; documents that fail the WHERE return null and are skipped.
        // HasComplexOperators is intentionally NOT checked here: VisitAggregate always
        // sets it so that queries that cannot be pushed down fall back to EnumerableRewriter.
        if (model.AggregateOp is not null && model.AggregateSelector is not null)
        {
            if (TryBsonAggregate<TResult>(model.AggregateOp, model.AggregateSelector, model.WhereClause, out var aggResult))
                return aggResult;
        }

        // ── Push-down SELECT (+ optional WHERE) ───────────────────────────────
        // Single-pass BSON projection: no T instantiation, no EnumerableRewriter.
        // Only applicable when the projection is the whole query: no post-scan ordering,
        // paging or other operators. A terminal after the Select (Any, Max, Count(pred), ...)
        // runs the usual way.
        if (model.SelectClause is not null
            && model.OrderByClause is null
            && !model.Take.HasValue
            && !model.Skip.HasValue
            && !model.HasComplexOperators
            && TryPushDownSelect<TResult>(model.SelectClause, model.WhereClause, out var pushed))
        {
            return pushed;
        }

        // 2. Data fetching.
        // When Take (+ optional Skip) is present without complex operators or ordering that
        // requires a full scan first, cap how many rows we need to materialise.
        int fetchLimit = (model.Take.HasValue && model.OrderByClause == null && !model.HasComplexOperators)
            ? (model.Skip.GetValueOrDefault() + model.Take.Value)
            : int.MaxValue;

        // ── Fast path: OrderBy(indexedField).Skip(S).Take(N) without WHERE ───────────
        // Reads only skip+take entries from the index in sorted order, skipping the
        // O(n log n) in-memory sort over all documents.  Skip is performed at the
        // index-entry level (no document reads for skipped positions), so only the
        // requested page window is ever deserialised.
        if (model.WhereClause == null && model.OrderByClause != null && model.Take.HasValue
            && TryGetIndexWindow(expression, model.OrderByClause, out int windowEnd, out bool plainWindow))
        {
            var orderOpt = IndexOptimizer.TryOptimizeOrderBy(model, _collection.GetIndexes());
            if (orderOpt is not null)
            {
                bool ascending = !model.OrderDescending;
                if (plainWindow && !model.HasComplexOperators
                    && (model.SelectClause == null || IsSequenceResult<TResult>()))
                {
                    int skip = model.Skip ?? 0;
                    int take = model.Take.Value;
                    var topN = new List<T>(take);
                    await foreach (var item in _collection.QueryIndexAsync(orderOpt.IndexName, null, null, ascending, skip, take, cancellationToken))
                        topN.Add(item);
                    if (model.SelectClause != null)
                        return ProjectEnumerable<TResult>(topN, model.SelectClause);
                    return TerminalReturn<TResult>(topN);
                }

                // Operators after the window (a terminal with a predicate or selector, a scalar
                // after Select, ...): read the first windowEnd entries in index order and replay the
                // whole query over them. OrderBy is a stable sort, so the replay keeps the index
                // order and its Skip/Take select the same window.
                // This needs the root queryable to be the query's only source: EnumerableRewriter
                // replaces every use of it with the prefix, so TryGetIndexWindow rejects a query
                // that reads a queryable again after the window (Concat(q), Join(q, ...)).
                var prefix = new List<T>(windowEnd);
                await foreach (var item in _collection.QueryIndexAsync(orderOpt.IndexName, null, null, ascending, 0, windowEnd, cancellationToken))
                    prefix.Add(item);
                return ExecuteViaEnumerableRewriter<TResult>(expression, prefix);
            }
        }

        // ── General path: FetchAsync picks index / BSON scan / full scan ─────
        // FetchAsync always applies the WHERE clause internally (all three strategies
        // filter before yielding), so no residual WHERE step is needed afterwards.
        var sourceList = new List<T>();
        bool whereAlreadyApplied = model.WhereClause != null;

        await foreach (var item in _collection.FetchAsync(model.WhereClause, fetchLimit, cancellationToken))
            sourceList.Add(item);

        IEnumerable<T> sourceData = sourceList;

        // ── Complex-operator fallback ──────────────────────────────────────────
        // GroupBy, Join, Sum/Average/Min/Max with selectors — operators the direct
        // pipeline cannot model.  We still benefit from index / BSON-scan fetch,
        // then let EnumerableRewriter translate the remaining Queryable calls to
        // Enumerable equivalents and compile the residual expression once.
        if (model.HasComplexOperators)
            return ExecuteViaEnumerableRewriter<TResult>(expression, sourceData);

        // When OrderBy is chained after Select, the key-selector parameter type is the
        // projected type (e.g. DateTimeOffset), not T.  The direct pipeline builds a
        // Func<T, object> from that lambda, which throws ArgumentException at runtime.
        // Fall back to EnumerableRewriter which rewrites the full expression tree correctly.
        if (model.OrderByClause != null && model.OrderByClause.Parameters[0].Type != typeof(T))
            return ExecuteViaEnumerableRewriter<TResult>(expression, sourceData);

        // The direct pipeline can only project into a sequence. A scalar terminal after a
        // Select (e.g. Take(n).Select(...).Count()) needs the full expression.
        if (model.SelectClause != null && !IsSequenceResult<TResult>())
            return ExecuteViaEnumerableRewriter<TResult>(expression, sourceData);

        // 3. Direct pipeline — no expression-tree rewrite, no EnumerableRewriter, no Compile().
        return ExecutePipeline<TResult>(model, sourceData, whereAlreadyApplied);
    }

    /// <summary>
    /// Applies WHERE / ORDER BY / SKIP / TAKE / SELECT directly on the materialized
    /// <see cref="IEnumerable{T}"/> source without rewriting the original expression tree.
    /// Terminal results (<c>IEnumerable&lt;T&gt;</c>, <c>List&lt;T&gt;</c>, scalar aggregates)
    /// are dispatched via a type-switch to avoid any reflection call per invocation.
    /// </summary>
    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    [RequiresUnreferencedCode("BLite LINQ queries use reflection to resolve methods and types at runtime. Ensure all entity types and their members are preserved.")]
    private TResult ExecutePipeline<TResult>(QueryModel model, IEnumerable<T> source, bool whereAlreadyApplied)
    {
        IEnumerable<T> data = source;

        // WHERE (residual — when the BSON scan didn't already filter)
        if (!whereAlreadyApplied && model.WhereClause != null)
        {
            var pred = model.WhereClause.Compile() as Func<T, bool>
                       ?? (Func<T, bool>)model.WhereClause.Compile();
            data = data.Where(pred);
        }

        // ORDER BY — compile key selector to Func<T, object> (boxing) to avoid
        // MakeGenericMethod for Enumerable.OrderBy<T,TKey> with runtime-only TKey.
        if (model.OrderByClause != null)
        {
            var param = model.OrderByClause.Parameters[0];
            var boxed = Expression.Lambda<Func<T, object>>(
                Expression.Convert(model.OrderByClause.Body, typeof(object)), param).Compile();
            data = model.OrderDescending
                ? data.OrderByDescending(x => (IComparable?)boxed(x))
                : data.OrderBy(x => (IComparable?)boxed(x));
        }

        // SKIP / TAKE
        if (model.Skip.HasValue) data = data.Skip(model.Skip.Value);
        if (model.Take.HasValue) data = data.Take(model.Take.Value);

        // SELECT (with ORDER BY / SKIP / TAKE — rare path, push-down already handled the common case)
        if (model.SelectClause != null)
            return ProjectEnumerable<TResult>(data, model.SelectClause);

        // Terminal dispatch — no reflection, no Compile(), no MakeGenericMethod.
        return TerminalReturn<TResult>(data);
    }

    /// <summary>
    /// Projects <paramref name="source"/> via the given SELECT lambda.
    /// The generic dispatch (<c>ProjectTyped&lt;TProj&gt;</c>) is resolved via a cached
    /// <see cref="MethodInfo"/>: <c>MakeGenericMethod</c> is called once per projection type.
    /// </summary>
    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    [RequiresUnreferencedCode("BLite LINQ queries use reflection to resolve methods and types at runtime. Ensure all entity types and their members are preserved.")]
    private TResult ProjectEnumerable<TResult>(IEnumerable<T> source, LambdaExpression selectLambda)
    {
        if (!IsSequenceResult<TResult>())
            throw new NotSupportedException(
                $"BLite query pipeline: cannot project into result type '{typeof(TResult)}'.");

        var projType = typeof(TResult).GetGenericArguments()[0];
        var method = s_projectMethodCache.GetOrAdd(
            projType,
            t => typeof(BTreeQueryProvider<TId, T>)
                .GetMethod(nameof(ProjectTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(t));

        return (TResult)method.Invoke(null, [source, selectLambda])!;
    }

    /// <summary>Compiles <paramref name="selectLambda"/> as <c>Func&lt;T, TProj&gt;</c> and projects the source.</summary>
    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    private static IEnumerable<TProj> ProjectTyped<TProj>(IEnumerable<T> source, LambdaExpression selectLambda)
    {
        var selector = (Func<T, TProj>)selectLambda.Compile();
        return source.Select(selector);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo>
        s_projectMethodCache = new();

    /// <summary>Whether <typeparamref name="TResult"/> is <c>IEnumerable&lt;TElement&gt;</c>, i.e. the query is enumerated rather than ended by a terminal.</summary>
    private static bool IsSequenceResult<TResult>()
        => typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(IEnumerable<>);

    /// <summary>
    /// Checks that <paramref name="expression"/> starts with <c>OrderBy</c>/<c>OrderByDescending</c>
    /// on the collection (the call whose key selector is <paramref name="orderBy"/>), followed by
    /// <c>Skip</c>/<c>Take</c> calls that bound it, so that the result only depends on the first
    /// <paramref name="windowEnd"/> documents in that order.
    /// </summary>
    /// <param name="plainWindow">
    /// <c>true</c> when the window is exactly <c>Take(n)</c> or <c>Skip(s).Take(n)</c>, as
    /// <see cref="QueryModel.Skip"/> and <see cref="QueryModel.Take"/> describe it, and only
    /// <c>Select</c> or a plain <c>Count()</c>/<c>LongCount()</c> follows it.
    /// </param>
    private static bool TryGetIndexWindow(Expression expression, LambdaExpression orderBy,
                                          out int windowEnd, out bool plainWindow)
    {
        windowEnd = 0;
        plainWindow = false;

        // Collect the Queryable calls from the root outwards.
        var calls = new List<MethodCallExpression>();
        var current = expression;
        while (current is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            calls.Add(call);
            current = call.Arguments[0];
        }
        if (current is not ConstantExpression { Value: IQueryable } || calls.Count < 2) return false;
        calls.Reverse();

        var first = calls[0];
        if (first.Method.Name is not ("OrderBy" or "OrderByDescending")
            || first.Arguments[1] is not UnaryExpression { Operand: var key }
            || !ReferenceEquals(key, orderBy))
            return false;

        long start = 0, length = long.MaxValue;
        int i = 1;
        for (; i < calls.Count && calls[i].Method.Name is "Skip" or "Take"; i++)
        {
            if (calls[i].Arguments[1] is not ConstantExpression { Value: int n }) return false;
            n = Math.Max(n, 0);
            if (calls[i].Method.Name == "Skip")
            {
                start += n;
                if (length != long.MaxValue) length = Math.Max(0, length - n);
            }
            else
            {
                length = Math.Min(length, n);
            }
        }

        // No Take bounds the window, or a ThenBy refines the order of the documents at its edge.
        if (length == long.MaxValue) return false;
        if (i < calls.Count && calls[i].Method.Name is "ThenBy" or "ThenByDescending") return false;

        // A second query source after the window (Concat(q), Join(q, ...), Union(q.Select(...))):
        // the replay would read the window prefix for it, since every use of the root is replaced.
        for (int j = i; j < calls.Count; j++)
            for (int a = 1; a < calls[j].Arguments.Count; a++)
                if (ContainsQueryableConstant(calls[j].Arguments[a])) return false;

        long end = start + length;
        if (end > int.MaxValue) return false;
        windowEnd = (int)end;

        var window = calls.GetRange(1, i - 1).ConvertAll(c => c.Method.Name);
        plainWindow = (window.Count == 1 && window[0] == "Take"
                       || window.Count == 2 && window[0] == "Skip" && window[1] == "Take")
                      && calls.Skip(i).All(c => c.Method.Name == "Select"
                                                || c.Method.Name is "Count" or "LongCount" && c.Arguments.Count == 1);
        return true;
    }

    /// <summary>Whether <paramref name="expression"/> contains a constant holding an <see cref="IQueryable"/>, i.e. a query source.</summary>
    private static bool ContainsQueryableConstant(Expression expression)
    {
        var finder = new QueryableConstantFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class QueryableConstantFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression? Visit(Expression? node) => Found ? node : base.Visit(node);

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is IQueryable) Found = true;
            return node;
        }
    }

    /// <summary>
    /// Type-switch dispatch for terminal results — first-match, no reflection per call.
    /// Covers all standard LINQ terminal operators: First/Single/Count/Any/All/ToList/ToArray.
    /// </summary>
    private static TResult TerminalReturn<TResult>(IEnumerable<T> data)
    {
        if (typeof(TResult) == typeof(IEnumerable<T>)) return (TResult)data;
        if (typeof(TResult) == typeof(List<T>)) return (TResult)(object)data.ToList();
        if (typeof(TResult) == typeof(T[])) return (TResult)(object)data.ToArray();
        if (typeof(TResult) == typeof(T)) return (TResult)(object)data.First();
        if (typeof(TResult) == typeof(int)) return (TResult)(object)data.Count();
        if (typeof(TResult) == typeof(long)) return (TResult)(object)(long)data.Count();
        if (typeof(TResult) == typeof(bool)) return (TResult)(object)data.Any();
        if (typeof(TResult) == typeof(object)) return (TResult)(object)(data.ToList());

        // Unknown terminal: materialise and attempt a cast — handles e.g. First<SubType>.
        var list = data.ToList();
        if (list.Count > 0 && list[0] is TResult first) return first;
        if (list is TResult asResult) return asResult;

        throw new NotSupportedException(
            $"BLite query pipeline: unsupported terminal result type '{typeof(TResult)}'.");
    }

    // ─── BSON-level aggregate push-down ──────────────────────────────────────

    /// <summary>
    /// Attempts to compute Sum or Average entirely by scanning raw BSON bytes,
    /// without deserialising the full entity <typeparamref name="T"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> and a populated <paramref name="result"/> on success;
    /// <c>false</c> to signal the caller should fall through to the standard path.
    /// </returns>
    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    [RequiresUnreferencedCode("BLite LINQ queries use reflection to resolve methods and types at runtime. Ensure all entity types and their members are preserved.")]
    private bool TryBsonAggregate<TResult>(
        string aggregateOp,
        LambdaExpression selector,
        LambdaExpression? whereClause,
        out TResult result)
    {
        result = default!;
        var fieldType = selector.ReturnType;

        // Only numeric types are supported; others fall through to EnumerableRewriter.
        if (fieldType != typeof(decimal) && fieldType != typeof(double) &&
            fieldType != typeof(float) && fieldType != typeof(int) &&
            fieldType != typeof(long))
            return false;

        // Compile a BSON projector for the target field.
        // When whereClause is provided, BsonProjectionCompiler merges WHERE + SELECT fields
        // into one BSON pass; documents that fail the WHERE return null and are skipped
        // by ScanAsync<FieldType>.
        object? projector;
        try
        {
            var compileMethod = s_compiledSelectMethods.GetOrAdd(
                fieldType, t => s_tryCompileMethod.MakeGenericMethod(typeof(T), t));
            projector = compileMethod.Invoke(null, [selector, whereClause, _collection.GetKeyMap()]);
        }
        catch { return false; }

        if (projector is null) return false; // field shape too complex for BSON push-down

        // Execute the BSON scan — yields only non-null field values.
        System.Collections.IEnumerable? values;
        try
        {
            var scanMethod = s_compiledScanMethods.GetOrAdd(
                fieldType, t => s_scanProjectorMethod.MakeGenericMethod(t));
            values = scanMethod.Invoke(_collection, [projector]) as System.Collections.IEnumerable;
        }
        catch { return false; }

        if (values is null) return false;

        try { return AggregateValues<TResult>(aggregateOp, values, fieldType, out result); }
        catch { return false; }
    }

    private static bool AggregateValues<TResult>(
        string op,
        System.Collections.IEnumerable values,
        Type fieldType,
        out TResult result)
    {
        result = default!;

        if (fieldType == typeof(decimal))
        {
            var typed = (IEnumerable<decimal>)values;
            decimal agg = op switch
            {
                "Sum"     => typed.Sum(),
                "Average" => typed.DefaultIfEmpty().Average(),
                "Min"     => typed.DefaultIfEmpty().Min(),
                "Max"     => typed.DefaultIfEmpty().Max(),
                _         => throw new NotSupportedException($"Unsupported aggregate op: {op}")
            };
            if (typeof(TResult) == typeof(decimal)) { result = (TResult)(object)agg; return true; }
        }
        else if (fieldType == typeof(double))
        {
            var typed = (IEnumerable<double>)values;
            double agg = op switch
            {
                "Sum"     => typed.Sum(),
                "Average" => typed.DefaultIfEmpty().Average(),
                "Min"     => typed.DefaultIfEmpty().Min(),
                "Max"     => typed.DefaultIfEmpty().Max(),
                _         => throw new NotSupportedException($"Unsupported aggregate op: {op}")
            };
            if (typeof(TResult) == typeof(double)) { result = (TResult)(object)agg; return true; }
        }
        else if (fieldType == typeof(float))
        {
            var typed = (IEnumerable<float>)values;
            float agg = op switch
            {
                "Sum"     => typed.Sum(),
                "Average" => typed.DefaultIfEmpty().Average(),
                "Min"     => typed.DefaultIfEmpty().Min(),
                "Max"     => typed.DefaultIfEmpty().Max(),
                _         => throw new NotSupportedException($"Unsupported aggregate op: {op}")
            };
            if (typeof(TResult) == typeof(float)) { result = (TResult)(object)agg; return true; }
        }
        else if (fieldType == typeof(int))
        {
            var typed = (IEnumerable<int>)values;
            if (op == "Sum")
            {
                long sum = 0;
                foreach (var v in typed) sum += v;
                if (typeof(TResult) == typeof(long)) { result = (TResult)(object)sum; return true; }
                if (typeof(TResult) == typeof(int)) { result = (TResult)(object)(int)sum; return true; }
            }
            else if (op == "Average")
            {
                double avg = typed.DefaultIfEmpty().Average();
                if (typeof(TResult) == typeof(double)) { result = (TResult)(object)avg; return true; }
            }
            else if (op == "Min")
            {
                int min = typed.DefaultIfEmpty().Min();
                if (typeof(TResult) == typeof(int)) { result = (TResult)(object)min; return true; }
            }
            else if (op == "Max")
            {
                int max = typed.DefaultIfEmpty().Max();
                if (typeof(TResult) == typeof(int)) { result = (TResult)(object)max; return true; }
            }
        }
        else if (fieldType == typeof(long))
        {
            var typed = (IEnumerable<long>)values;
            if (op == "Sum")
            {
                long sum = 0;
                foreach (var v in typed) sum += v;
                if (typeof(TResult) == typeof(long)) { result = (TResult)(object)sum; return true; }
            }
            else if (op == "Average")
            {
                double avg = typed.DefaultIfEmpty().Select(x => (double)x).Average();
                if (typeof(TResult) == typeof(double)) { result = (TResult)(object)avg; return true; }
            }
            else if (op == "Min")
            {
                long min = typed.DefaultIfEmpty().Min();
                if (typeof(TResult) == typeof(long)) { result = (TResult)(object)min; return true; }
            }
            else if (op == "Max")
            {
                long max = typed.DefaultIfEmpty().Max();
                if (typeof(TResult) == typeof(long)) { result = (TResult)(object)max; return true; }
            }
        }
        return false;
    }

    // ─── Push-down SELECT helper ──────────────────────────────────────────────

    /// <summary>
    /// Attempts to execute the query entirely via a single-pass BSON projection scan,
    /// bypassing full <typeparamref name="T"/> deserialisation.
    /// </summary>
    /// <returns>
    /// <c>true</c> and the projected <c>IEnumerable&lt;TProj&gt;</c> in <paramref name="result"/>;
    /// <c>false</c> if push-down is not applicable (caller should fall through to the standard path).
    /// <c>default(TResult)</c> cannot signal this: for a value-type result such as <c>bool</c> it
    /// is a valid answer (#181).
    /// </returns>
    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    [RequiresUnreferencedCode("BLite LINQ queries use reflection to resolve methods and types at runtime. Ensure all entity types and their members are preserved.")]
    private bool TryPushDownSelect<TResult>(LambdaExpression selectLambda,
                                            LambdaExpression? whereLambda,
                                            out TResult result)
    {
        result = default!;

        // We need TResult = IEnumerable<TProj> for some projection type TProj.
        if (!IsSequenceResult<TResult>()) return false;

        var projType = typeof(TResult).GetGenericArguments()[0];
        if (projType == typeof(T)) return false; // not a real projection

        // Compile the push-down projector via reflection (TProj is only known at runtime).
        // BsonProjectionCompiler.TryCompile<T, TProj>(selectLambda, whereLambda?) handles
        // both the pure-SELECT and the WHERE+SELECT cases in a single BSON pass.
        object? projector;
        try
        {
            var compileMethod = s_compiledSelectMethods.GetOrAdd(
                projType,
                t => s_tryCompileMethod.MakeGenericMethod(typeof(T), t));
            projector = compileMethod.Invoke(null, [selectLambda, whereLambda, _collection.GetKeyMap()]);
        }
        catch { return false; }

        if (projector is null) return false; // compilation soft-failed

        // Invoke _collection.Scan<TProj>(projector) — MethodInfo cached per TProj.
        try
        {
            var scanMethod = s_compiledScanMethods.GetOrAdd(
                projType,
                t => s_scanProjectorMethod.MakeGenericMethod(t));
            if (scanMethod.Invoke(_collection, [projector]) is not TResult scanned) return false;
            result = scanned;
            return true;
        }
        catch { return false; }
    }

    // ─── Expression-tree helpers ──────────────────────────────────────────────

    /// <summary>
    /// Fallback for queries containing operators the direct pipeline cannot handle
    /// (GroupBy, Join, Sum/Average/Min/Max with selectors, etc.).
    /// The source data has already been fetched (and filtered by index/BSON-scan),
    /// so we only need to rewrite the remaining <c>Queryable.*</c> calls to
    /// <c>Enumerable.*</c> equivalents before compiling and executing.
    /// </summary>
    /// <remarks>
    /// The compiled <c>Func&lt;IEnumerable&lt;T&gt;, TResult&gt;</c> is cached per expression shape
    /// (keyed by <c>TResult.FullName + expression.ToString()</c>), so the expensive
    /// <see cref="Expression.Compile"/> call is paid only once per unique query shape.
    /// </remarks>
    [RequiresDynamicCode("BLite LINQ queries use Expression.Compile() and MakeGenericMethod which require dynamic code generation.")]
    private TResult ExecuteViaEnumerableRewriter<TResult>(Expression expression, IEnumerable<T> sourceData)
    {
        var rootFinder = new RootFinder();
        rootFinder.Visit(expression);
        var root = rootFinder.Root;
        if (root == null)
            throw new InvalidOperationException("Could not find root IQueryable in expression.");

        var cacheKey = typeof(TResult).FullName + "\x00" + expression.ToString();

        var cachedDelegate = s_enumRewriterCache.GetOrAdd(cacheKey, _ =>
        {
            var sourceParam = Expression.Parameter(typeof(IEnumerable<T>), "_src");
            var rewriter = new EnumerableRewriter(root, sourceParam);
            var rewritten = rewriter.Visit(expression);
            if (rewritten.Type != typeof(TResult))
                rewritten = Expression.Convert(rewritten, typeof(TResult));
            return Expression.Lambda<Func<IEnumerable<T>, TResult>>(rewritten, sourceParam).Compile();
        });

        return ((Func<IEnumerable<T>, TResult>)cachedDelegate)(sourceData);
    }

    /// <summary>Cache of compiled EnumerableRewriter delegates keyed by expression shape.</summary>
    private static readonly ConcurrentDictionary<string, Delegate> s_enumRewriterCache = new();

    private sealed class RootFinder : ExpressionVisitor
    {
        public IQueryable? Root { get; private set; }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (Root == null && node.Value is IQueryable q)
                Root = q;
            return base.VisitConstant(node);
        }
    }

    // ─── IBTreeQueryCore<T> implementation ───────────────────────────────────

    internal DocumentCollection<TId, T> Collection => _collection;

    IAsyncEnumerable<T> IBTreeQueryCore<T>.ScanAsync(BsonReaderPredicate predicate, CancellationToken ct)
        => _collection.ScanAsync(predicate, ct);

    IAsyncEnumerable<T> IBTreeQueryCore<T>.ScanAsync(IndexQueryPlan plan, CancellationToken ct)
        => _collection.ScanAsync(plan, ct);

    IEnumerable<CollectionIndexInfo> IBTreeQueryCore<T>.GetIndexes()
        => _collection.GetIndexes();

    Task<int> IBTreeQueryCore<T>.CountAsync(CancellationToken ct)
        => _collection.CountAsync(ct);

    ValueTask<TResult> IBTreeQueryCore<T>.MinBoundaryAsync<TResult>(IndexMinMax plan, CancellationToken ct)
        => _collection.MinBoundaryAsync<TResult>(plan, ct);

    ValueTask<TResult> IBTreeQueryCore<T>.MaxBoundaryAsync<TResult>(IndexMinMax plan, CancellationToken ct)
        => _collection.MaxBoundaryAsync<TResult>(plan, ct);
}
