using System;

namespace BLite.Core;

/// <summary>
/// Thrown by a collection write method when it rejects its input before writing anything:
/// a duplicate primary key, a duplicate value for a unique index, a vector whose length does not
/// match its vector index, or a document or index key that is too large to be stored.
/// </summary>
/// <remarks>
/// <para>
/// This is the only exception after which a write that ran in a caller-owned transaction leaves
/// that transaction usable. A single-document write (insert, update, upsert) that throws it wrote
/// nothing, so the caller can catch it, carry on with the same transaction and commit. A bulk write
/// that throws it on its first document wrote nothing either; one that throws it after earlier
/// documents of the batch were written rolls the transaction back like any other failure.
/// </para>
/// <para>
/// Any other exception thrown while a write method is modifying pages rolls the caller's
/// transaction back (<see cref="Transactions.ITransaction.State"/> becomes
/// <see cref="Transactions.TransactionState.Aborted"/>), because the failed write may already have
/// updated the data page and some of the indexes but not the others, and committing that would
/// leave the indexes out of step with the data.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/>, the type these errors had before, except
/// that a vector dimension mismatch used to surface as an <see cref="ArgumentException"/>.
/// </para>
/// </remarks>
public class BLiteValidationException : InvalidOperationException
{
    /// <summary>Creates the exception with a message describing the rejected input.</summary>
    public BLiteValidationException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error that caused it.</summary>
    public BLiteValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
