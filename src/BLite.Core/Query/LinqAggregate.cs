namespace BLite.Core.Query;

/// <summary>The LINQ aggregate operators that can run without materialising entities.</summary>
internal enum AggregateOperator
{
    Sum,
    Average,
    Min,
    Max,
}

/// <summary>
/// Folds the values of one numeric property the way the <c>Enumerable</c> operator does when LINQ
/// runs it over the materialised entities, so a pushed-down aggregate returns the same value and
/// throws the same exceptions:
/// <list type="bullet">
///   <item><c>Sum</c> of <c>int</c> accumulates in a checked <c>int</c>, of <c>long</c> in a checked
///   <c>long</c>, of <c>float</c> in a <c>double</c> (cast back at the end); <c>Average</c> of
///   <c>int</c>/<c>long</c> sums in a checked <c>long</c>.</item>
///   <item><c>Min</c>/<c>Max</c> compare with <see cref="IComparable{T}"/> (as
///   <c>Comparer&lt;T&gt;.Default</c>) and only replace on a strict improvement, so the first of
///   equal values wins (<c>-0.0</c> vs <c>0.0</c>, <c>1.0m</c> vs <c>1.00m</c>) and <c>NaN</c> is the
///   smallest value.</item>
///   <item>Nullable selectors skip <c>null</c>; on no values <c>Sum</c> is <c>0</c> and the others
///   return <c>null</c>. Non-nullable <c>Average</c>/<c>Min</c>/<c>Max</c> over no values throw
///   <see cref="InvalidOperationException"/>.</item>
/// </list>
/// Values must be added in the order LINQ would see them, since floating-point sums and checked
/// overflow depend on it. With <c>exactTotal</c>, integer sums are instead computed exactly and only
/// the total is range-checked: for values read in a different order (from an index) this throws
/// <see cref="OverflowException"/> exactly when the result does not fit.
/// </summary>
internal sealed class LinqAggregate<TValue> where TValue : struct, IComparable<TValue>
{
    private readonly AggregateOperator _op;
    private readonly bool _nullable;
    private readonly bool _exactTotal;

    private long _count;
    private TValue _best;
    private int _sumInt32;
    private long _sumInt64;
    private decimal _sumOverflow; // exactTotal: the part of a long sum beyond the long range
    private double _sumDouble;
    private decimal _sumDecimal;

    public LinqAggregate(AggregateOperator op, bool nullable, bool exactTotal = false)
    {
        _op = op;
        _nullable = nullable;
        _exactTotal = exactTotal;
    }

    /// <summary>
    /// The result type of the <c>Queryable</c> operator for a <typeparamref name="TValue"/>
    /// (or nullable) selector, e.g. <c>double</c> for <c>Average</c> of <c>int</c>.
    /// </summary>
    public static Type ResultType(AggregateOperator op, bool nullable)
    {
        if (op == AggregateOperator.Average && (typeof(TValue) == typeof(int) || typeof(TValue) == typeof(long)))
            return nullable ? typeof(double?) : typeof(double);
        return nullable ? typeof(TValue?) : typeof(TValue);
    }

    /// <summary>Adds the value of the next document (nullable selectors pass <c>null</c> as such).</summary>
    public void Add(TValue? value)
    {
        if (value.HasValue) Add(value.GetValueOrDefault());
    }

    /// <summary>Adds the value of the next document.</summary>
    public void Add(TValue value)
    {
        switch (_op)
        {
            case AggregateOperator.Min:
                if (_count == 0 || value.CompareTo(_best) < 0) _best = value;
                break;
            case AggregateOperator.Max:
                if (_count == 0 || value.CompareTo(_best) > 0) _best = value;
                break;
            default:
                Accumulate(value);
                break;
        }
        _count++;
    }

    private void Accumulate(TValue value)
    {
        if (typeof(TValue) == typeof(int))
        {
            int v = (int)(object)value;
            if (_op == AggregateOperator.Sum && !_exactTotal) _sumInt32 = checked(_sumInt32 + v);
            else _sumInt64 = checked(_sumInt64 + v);
        }
        else if (typeof(TValue) == typeof(long))
        {
            long v = (long)(object)value;
            if (!_exactTotal)
            {
                _sumInt64 = checked(_sumInt64 + v);
            }
            else
            {
                long sum = unchecked(_sumInt64 + v);
                // Signed overflow: both operands share a sign the result does not have.
                if (((_sumInt64 ^ sum) & (v ^ sum)) < 0)
                {
                    _sumOverflow += (decimal)_sumInt64 + v;
                    sum = 0;
                }
                _sumInt64 = sum;
            }
        }
        else if (typeof(TValue) == typeof(double))
        {
            _sumDouble += (double)(object)value;
        }
        else if (typeof(TValue) == typeof(float))
        {
            _sumDouble += (float)(object)value;
        }
        else if (typeof(TValue) == typeof(decimal))
        {
            _sumDecimal += (decimal)(object)value;
        }
        else
        {
            throw new NotSupportedException($"Aggregating {typeof(TValue)} is not supported.");
        }
    }

    /// <summary>
    /// The aggregate, boxed as <see cref="ResultType"/> (a nullable result boxes its value or is
    /// <c>null</c>).
    /// </summary>
    public object? Result()
    {
        switch (_op)
        {
            case AggregateOperator.Sum:
                if (typeof(TValue) == typeof(int)) return _exactTotal ? checked((int)_sumInt64) : _sumInt32;
                if (typeof(TValue) == typeof(long)) return ExactInt64();
                if (typeof(TValue) == typeof(double)) return _sumDouble;
                if (typeof(TValue) == typeof(float)) return (float)_sumDouble;
                return _sumDecimal;

            case AggregateOperator.Average:
                if (_count == 0) return NoElements();
                if (typeof(TValue) == typeof(int)) return (double)_sumInt64 / _count;
                if (typeof(TValue) == typeof(long)) return (double)ExactInt64() / _count;
                if (typeof(TValue) == typeof(double)) return _sumDouble / _count;
                if (typeof(TValue) == typeof(float)) return (float)(_sumDouble / _count);
                return _sumDecimal / _count;

            default:
                return _count == 0 ? NoElements() : _best;
        }
    }

    private long ExactInt64() => _sumOverflow == 0 ? _sumInt64 : checked((long)(_sumOverflow + _sumInt64));

    private object? NoElements() => _nullable
        ? null
        : throw new InvalidOperationException("Sequence contains no elements");
}
