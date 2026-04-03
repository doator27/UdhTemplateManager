using Microsoft.Data.Sqlite;
using System;
using System.Threading;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Provides retry logic for SQLite write operations that may fail with a busy or locked error
/// when multiple users access the same database simultaneously.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Executes <paramref name="action"/> and retries up to <paramref name="maxRetries"/> times
    /// when a <see cref="SqliteException"/> with error code <c>SQLITE_BUSY</c> (5) or
    /// <c>SQLITE_LOCKED</c> (6) is thrown. Delays between retries grow exponentially.
    /// Re-throws the original exception if all retries are exhausted.
    /// </summary>
    /// <param name="action">The database write operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3).</param>
    /// <param name="baseDelayMs">Base delay in milliseconds between retries (default: 200).</param>
    public static void ExecuteWithRetry(Action action, int maxRetries = 3, int baseDelayMs = 200)
    {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (SqliteException ex) when (attempt < maxRetries && IsBusyError(ex))
            {
                Thread.Sleep(baseDelayMs * (int)Math.Pow(2, attempt));
            }
        }
    }

    /// <summary>
    /// Executes <paramref name="func"/> and retries up to <paramref name="maxRetries"/> times
    /// when a <see cref="SqliteException"/> with error code <c>SQLITE_BUSY</c> or
    /// <c>SQLITE_LOCKED</c> is thrown.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="func">The database read or write operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3).</param>
    /// <param name="baseDelayMs">Base delay in milliseconds between retries (default: 200).</param>
    /// <returns>The result of <paramref name="func"/>.</returns>
    public static T ExecuteWithRetry<T>(Func<T> func, int maxRetries = 3, int baseDelayMs = 200)
    {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return func();
            }
            catch (SqliteException ex) when (attempt < maxRetries && IsBusyError(ex))
            {
                Thread.Sleep(baseDelayMs * (int)Math.Pow(2, attempt));
            }
        }

        // Unreachable — the loop above always either returns or re-throws on the final attempt.
        throw new InvalidOperationException("RetryHelper: unreachable code path.");
    }

    /// <summary>Returns true for SQLite error codes that indicate a transient lock contention.</summary>
    private static bool IsBusyError(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY = 5, SQLITE_LOCKED = 6
}
