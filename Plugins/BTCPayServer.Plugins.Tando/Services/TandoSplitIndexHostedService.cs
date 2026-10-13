using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;
using BTCPayServer.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Dapper;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoSplitIndexHostedService(ApplicationDbContextFactory dbContextFactory, ILogger<TandoSplitIndexHostedService> logger) : IHostedService
{
    // Must stay in step with the WHERE and ORDER BY in TandoSplitService.ListSplits, or Postgres won't use it.
    private const string CreateIndexSql =
        """
        CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Invoices_TandoSplit"
        ON "Invoices" ("StoreDataId", ("Blob2"->'metadata'->'tandoSplit'->>'RecordedAt') DESC, "Id")
        WHERE ("Blob2"->'metadata'->'tandoSplit') IS NOT NULL
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // CONCURRENTLY doesn't lock the Invoices table but can take a while on a large instance, so don't hold up startup.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var ctx = dbContextFactory.CreateContext();
                await ctx.Database.GetDbConnection().ExecuteAsync(CreateIndexSql, commandTimeout: 0);
            }
            catch (Exception ex)
            {
                // Listing still works without the index, just slower on big stores.
                logger.LogWarning(ex, "Tando: could not create the split listing index");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}