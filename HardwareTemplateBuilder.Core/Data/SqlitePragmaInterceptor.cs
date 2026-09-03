using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// EF Core connection interceptor that applies per-connection SQLite PRAGMAs
/// immediately after each connection is opened.
/// <list type="bullet">
///   <item><c>busy_timeout=10000</c> — waits up to 10 seconds before returning a lock error,
///   allowing concurrent users to queue rather than fail immediately.</item>
/// </list>
/// Journal mode (DELETE — safe for shared network drives) is set once at startup via
/// <see cref="DatabaseInitializer"/> because it persists in the database file and does not
/// need to be reapplied per connection.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <inheritdoc/>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    /// <inheritdoc/>
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000;";
        cmd.ExecuteNonQuery();
    }
}
