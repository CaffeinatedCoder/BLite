using System;

namespace BLite.Core.Storage;

/// <summary>
/// Configures lock acquisition timeouts for database operations.
/// <para>
/// When a lock cannot be acquired within the specified timeout, a
/// <see cref="TimeoutException"/> is thrown — analogous to SQLite's
/// <c>SQLITE_BUSY</c> result code.
/// </para>
/// <para>
/// Use <see cref="Immediate"/> for fail-fast behaviour (SQLite's default when
/// <c>busy_timeout</c> is 0) or specify custom values to give concurrent callers
/// time to release contended locks.
/// </para>
/// </summary>
public readonly struct LockTimeout : IEquatable<LockTimeout>
{
    /// <summary>
    /// Timeout for acquiring read locks (milliseconds).
    /// <list type="bullet">
    ///   <item><c>0</c> — immediate failure if the lock is not available (SQLITE_BUSY).</item>
    ///   <item><c>-1</c> — wait indefinitely.</item>
    ///   <item><c>&gt;0</c> — wait up to this many milliseconds.</item>
    /// </list>
    /// </summary>
    public int ReadTimeoutMs { get; init; }

    /// <summary>
    /// Timeout for acquiring write locks (milliseconds).
    /// <list type="bullet">
    ///   <item><c>0</c> — immediate failure if the lock is not available (SQLITE_BUSY).</item>
    ///   <item><c>-1</c> — wait indefinitely.</item>
    ///   <item><c>&gt;0</c> — wait up to this many milliseconds.</item>
    /// </list>
    /// </summary>
    public int WriteTimeoutMs { get; init; }

    /// <summary>
    /// Maximum number of threads that can simultaneously enter the commit path.
    /// <para>
    /// Acts as an admission gate: when more than <see cref="MaxConcurrentWriters"/>
    /// threads attempt to commit at the same time, the excess threads wait on the
    /// gate semaphore (cheap, low-contention) instead of piling up on the internal
    /// WAL/commit locks where deep queues cause latency spikes and timeouts.
    /// </para>
    /// <list type="bullet">
    ///   <item><c>0</c> — no admission control (unlimited).</item>
    ///   <item><c>&gt;0</c> — at most this many concurrent commit operations.</item>
    /// </list>
    /// </summary>
    public int MaxConcurrentWriters { get; init; }

    /// <summary>
    /// Immediate failure on contention — equivalent to SQLite's default
    /// (<c>busy_timeout = 0</c>, returns <c>SQLITE_BUSY</c>).
    /// Suitable when the caller implements its own retry/back-off logic.
    /// </summary>
    public static LockTimeout Immediate => new() { ReadTimeoutMs = 0, WriteTimeoutMs = 0, MaxConcurrentWriters = 0 };

    /// <summary>
    /// Balanced default: 5 s write timeout, 5 s read timeout, 96 max concurrent writers.
    /// <para>
    /// A writer waits for other writers' I/O, not only for their CPU work: an auto-committing
    /// write holds the collection lock (in single-file mode, typed collections share one lock for
    /// the whole file) until its commit is in the WAL, and a checkpoint holds the page-file write
    /// lock while it syncs the data file. Writers queued behind each other wait for several such
    /// writes, and a disk can stall for hundreds of milliseconds. 5 s matches the WAL's own lock
    /// timeout, so a <see cref="TimeoutException"/> means a writer that is stuck (a deadlock or a
    /// hung disk), not one that is slow.
    /// </para>
    /// <para>
    /// The asynchronous write paths wait with <see cref="System.Threading.SemaphoreSlim.WaitAsync(int)"/>,
    /// which blocks no thread while waiting. Use <see cref="Immediate"/> for fail-fast
    /// (<c>SQLITE_BUSY</c>) behaviour, or <see cref="From"/> for other values.
    /// </para>
    /// </summary>
    public static LockTimeout Default => new() { ReadTimeoutMs = 5_000, WriteTimeoutMs = 5_000, MaxConcurrentWriters = 96 };

    /// <summary>
    /// Creates a <see cref="LockTimeout"/> from explicit millisecond values.
    /// </summary>
    public static LockTimeout From(int readTimeoutMs, int writeTimeoutMs, int maxConcurrentWriters = 96) =>
        new() { ReadTimeoutMs = readTimeoutMs, WriteTimeoutMs = writeTimeoutMs, MaxConcurrentWriters = maxConcurrentWriters };

    public bool Equals(LockTimeout other) =>
        ReadTimeoutMs == other.ReadTimeoutMs && WriteTimeoutMs == other.WriteTimeoutMs && MaxConcurrentWriters == other.MaxConcurrentWriters;

    public override bool Equals(object? obj) => obj is LockTimeout other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ReadTimeoutMs, WriteTimeoutMs, MaxConcurrentWriters);
    public static bool operator ==(LockTimeout left, LockTimeout right) => left.Equals(right);
    public static bool operator !=(LockTimeout left, LockTimeout right) => !left.Equals(right);

    public override string ToString() => $"LockTimeout(Read={ReadTimeoutMs}ms, Write={WriteTimeoutMs}ms, MaxWriters={MaxConcurrentWriters})";
}
