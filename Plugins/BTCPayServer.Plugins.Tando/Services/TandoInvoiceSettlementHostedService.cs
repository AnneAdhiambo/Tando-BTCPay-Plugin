using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoInvoiceSettlementHostedService(EventAggregator eventAggregator, IServiceScopeFactory scopeFactory,
    ILogger<TandoInvoiceSettlementHostedService> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    protected override void SubscribeToEvents()
    {
        this.Subscribe<InvoiceEvent>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not InvoiceEvent
            {
                EventCode: InvoiceEventCode.Completed or InvoiceEventCode.MarkedCompleted or InvoiceEventCode.PaidInFull,
                Invoice: { Status: InvoiceStatus.Settled } invoice
            })
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var merchantSettingsService = scope.ServiceProvider.GetRequiredService<TandoMerchantSettingsService>();
        var splitConfig = await merchantSettingsService.GetSplitConfig(invoice.StoreId);
        if (splitConfig.MpesaPercentage <= 0)
            return;

        var splitService = scope.ServiceProvider.GetRequiredService<TandoSplitService>();
        var (_, error) = await splitService.ComputeAndRecordSplit(invoice.StoreId, invoice.Id);
        if (error is not null)
            Logs.PayServer.LogWarning("Tando: could not record the split for invoice {InvoiceId}: {Error}", invoice.Id, error);
    }
}