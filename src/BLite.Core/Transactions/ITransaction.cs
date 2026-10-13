using System;
using System.Threading;
using System.Threading.Tasks;

namespace BLite.Core.Transactions;

/// <summary>
/// Public interface for database transactions.
/// Allows user-controlled transaction boundaries for batch operations.
/// </summary>
/// <example>
/// using (var txn = collection.BeginTransaction())
/// {
///     collection.Insert(entity1, txn);
///     collection.Insert(entity2, txn);
///     txn.Commit();
/// }
/// </example>
/// <remarks>
/// <para>
/// When a collection write method that was passed this transaction fails, what the caller can
/// rely on depends on the exception:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="BLiteValidationException"/> (duplicate primary or unique key, vector dimension
///     mismatch, document or index key too large): the input was rejected before anything was
///     written. The transaction is still <see cref="TransactionState.Active"/>; earlier writes in it
///     are intact and can be committed. For a bulk method this holds only if the first document
///     was rejected; otherwise the transaction is rolled back as below.
///   </description></item>
///   <item><description>
///     Any other exception thrown while the method was writing: the write may have been applied
///     to the data page and some indexes but not others, so the method rolls the transaction back
///     (<see cref="State"/> becomes <see cref="TransactionState.Aborted"/>). Later writes with it
///     throw <see cref="InvalidOperationException"/>, and so does <see cref="CommitAsync"/>.
///   </description></item>
/// </list>
/// <para>
/// There are no per-operation savepoints: a write that succeeded cannot be undone on its own.
/// </para>
/// </remarks>
public interface ITransaction : IDisposable
{
    /// <summary>
    /// Unique transaction identifier
    /// </summary>
    ulong TransactionId { get; }

    /// <summary>
    /// Current state of the transaction
    /// </summary>
    TransactionState State { get; }

    /// <summary>
    /// Asynchronously commits the transaction, making all changes permanent.
    /// </summary>
    ValueTask CommitAsync(CancellationToken ct = default);

    /// <summary>
    /// Rolls back the transaction, discarding all changes.
    /// Called automatically on Dispose() if Commit() was not called.
    /// </summary>
    ValueTask RollbackAsync();

    /// <summary>
    /// Adds a write operation to the current batch or transaction.
    /// </summary>
    /// <param name="operation">The write operation to add. Cannot be null.</param>
    void AddWrite(WriteOperation operation);

    /// <summary>
    /// Prepares the object for use by performing any necessary initialization or setup.
    /// </summary>
    /// <returns>true if the preparation was successful; otherwise, false.</returns>
    Task<bool> PrepareAsync();

    /// <summary>
    /// Event triggered when the transaction acts rollback.
    /// Useful for restoring in-memory state (like ID maps).
    /// </summary>
    event Action? OnRollback;
}
