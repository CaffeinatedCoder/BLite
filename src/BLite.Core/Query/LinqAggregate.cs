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
/// overflow depend on it. With <c>anyOrder</c>, <c>int</c>/<c>long</c> values may come in another
/// order (from an index): <c>Sum</c>/<c>Average</c> then keep the sums of the positive and of the
/// negative values apart. Every running total LINQ computes, in whatever order, lies between those
/// two, so when both fit the accumulator LINQ cannot overflow and the result is exact; otherwise
/// whether LINQ throws depends on the order and <see cref="DependsOnOrder"/> is set.
/// </summary>
internal sealed class LinqAggregate<TValue> where TValue : struct, IComparable<TValue>
{
    private readonly AggregateOperator _op;
    private readonly bool _nullable;
    private readonly bool _anyOrder;

    private long _count;
    private TValue _best;
    private int _sumInt32;
    private long _sumInt64;
    private long _sumPositive;    // anyOrder: the sums of the positive and the negative values
    private long _sumNegative;
    private bool _accumulatorOverflow;
    private double _sumDouble;
    private decimal _sumDecimal;

    public LinqAggregate(AggregateOperator op, bool nullable, bool anyOrder = false)
    {
        _op = op;
        _nullable = nullable;
        _anyOrder = anyOrder;
    }

    /// <summary>
    /// With <c>anyOrder</c>: whether the values added so far could make LINQ overflow in some order
    /// but not in another, so <see cref="Result"/> must not be used; the values have to be aggregated
    /// in the order LINQ sees them.
    /// </summary>
    public bool DependsOnOrder
    {
        get
        {
            if (!_anyOrder || _op is AggregateOperator.Min or AggregateOperator.Max) return false;
            if (_accumulatorOverflow) return true;
            // Sum of int accumulates in an int; Average of int and everything of long in a long.
            return typeof(TValue) == typeof(int) && _op == AggregateOperator.Sum
                && (_sumPositive > int.MaxValue || _sumNegative < int.MinValue);
        }
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
        if (_anyOrder && (typeof(TValue) == typeof(int) || typeof(TValue) == typeof(long)))
        {
            long v = typeof(TValue) == typeof(int) ? (int)(object)value : (long)(object)value;
            if (v >= 0)
            {
                long sum = unchecked(_sumPositive + v);
                if (sum < _sumPositive) _accumulatorOverflow = true;
                else _sumPositive = sum;
            }
            else
            {
                long sum = unchecked(_sumNegative + v);
                if (sum > _sumNegative) _accumulatorOverflow = true;
                else _sumNegative = sum;
            }
        }
        else if (typeof(TValue) == typeof(int))
        {
            int v = (int)(object)value;
            if (_op == AggregateOperator.Sum) _sumInt32 = checked(_sumInt32 + v);
            else _sumInt64 = checked(_sumInt64 + v);
        }
        else if (typeof(TValue) == typeof(long))
        {
            _sumInt64 = checked(_sumInt64 + (long)(object)value);
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
        if (DependsOnOrder)
            throw new InvalidOperationException("The result depends on the order of the values.");

        switch (_op)
        {
            case AggregateOperator.Sum:
                if (typeof(TValue) == typeof(int)) return _anyOrder ? (int)(_sumPositive + _sumNegative) : _sumInt32;
                if (typeof(TValue) == typeof(long)) return Int64Sum();
                if (typeof(TValue) == typeof(double)) return _sumDouble;
                if (typeof(TValue) == typeof(float)) return (float)_sumDouble;
                return _sumDecimal;

            case AggregateOperator.Average:
                if (_count == 0) return NoElements();
                if (typeof(TValue) == typeof(int) || typeof(TValue) == typeof(long)) return (double)Int64Sum() / _count;
                if (typeof(TValue) == typeof(double)) return _sumDouble / _count;
                if (typeof(TValue) == typeof(float)) return (float)(_sumDouble / _count);
                return _sumDecimal / _count;

            default:
                return _count == 0 ? NoElements() : _best;
        }
    }

    // anyOrder: a positive and a negative long, so their sum cannot overflow.
    private long Int64Sum() => _anyOrder ? _sumPositive + _sumNegative : _sumInt64;

    private object? NoElements() => _nullable
        ? null
        : throw new InvalidOperationException("Sequence contains no elements");
}
