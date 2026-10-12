using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using NBitpayClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

[JsonConverter(typeof(StringEnumConverter))]
public enum TandoPullPaymentStatus { NotApplicable, Created, Claimed, Failed }

[JsonConverter(typeof(StringEnumConverter))]
public enum TandoMpesaPayoutStatus { NotTriggered, Triggered, Confirmed, Failed }
public record TandoUpdateMpesaPayoutStatusRequest(TandoMpesaPayoutStatus Status, string? PayoutReference, string? Error);

public record TandoSplitRecord
{
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "";
    public decimal BtcPortionAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal MpesaPortionAmount { get; set; }
    public decimal MpesaPercentage { get; set; }
    public TandoMpesaDestinationType? MpesaDestinationType { get; set; }
    public string? MpesaDestination { get; set; }
    public bool MpesaSettled { get; set; }
    public DateTimeOffset? MpesaSettledAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? PullPaymentId { get; set; }
    public TandoPullPaymentStatus PullPaymentStatus { get; set; } = TandoPullPaymentStatus.NotApplicable;
    public string? PullPaymentError { get; set; }
    public TandoMpesaPayoutStatus MpesaPayoutStatus { get; set; } = TandoMpesaPayoutStatus.NotTriggered;
    public string? MpesaPayoutReference { get; set; }
    public string? MpesaPayoutError { get; set; }
}


public class TandoSplitService(InvoiceRepository invoiceRepository, TandoSubscriptionService subscriptionService,
    StoreRepository storeRepository, TandoMerchantSettingsService merchantSettingsService, ITandoMpesaPayoutClient tandoMpesaPayoutClient,
    PullPaymentHostedService pullPaymentHostedService)
{
    private const string SplitMetadataKey = "tandoSplit";

    private static readonly SemaphoreSlim SplitLock = new(1, 1);

    public async Task<(TandoSplitRecord? Record, string? Error)> ComputeAndRecordSplit(string storeId, string invoiceId)
    {
        await SplitLock.WaitAsync();
        try
        {
            return await ComputeAndRecordSplitCore(storeId, invoiceId);
        }
        finally
        {
            SplitLock.Release();
        }
    }

    private async Task<(TandoSplitRecord? Record, string? Error)> ComputeAndRecordSplitCore(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (null, "invoice_not_found");

        var existing = GetRecord(invoice);
        if (existing is not null)
            return (existing, null);

        if (invoice.Status != InvoiceStatus.Settled)
            return (null, "invoice_not_settled");

        var splitConfig = await merchantSettingsService.GetSplitConfig(storeId);
        if (splitConfig.MpesaPercentage > 0 && !(await subscriptionService.GetSettings()).IsTreasuryConfigured)
            return (null, "treasury_not_configured");
        var mpesaSettings = await merchantSettingsService.GetMpesaSettings(storeId);

        var paidAmount = Math.Max(0m, invoice.PaidAmount?.Net ?? 0m);
        var splitBase = Math.Min(paidAmount, invoice.Price);
        var mpesaPortion = Math.Round(splitBase * (splitConfig.MpesaPercentage / 100m), 2);
        var record = new TandoSplitRecord
        {
            TotalAmount = invoice.Price,
            PaidAmount = paidAmount,
            Currency = invoice.Currency,
            MpesaPortionAmount = mpesaPortion,
            BtcPortionAmount = invoice.Price - mpesaPortion,
            MpesaPercentage = splitConfig.MpesaPercentage,
            MpesaDestinationType = mpesaSettings?.DestinationType,
            MpesaDestination = mpesaSettings?.Destination,
            MpesaSettled = mpesaPortion <= 0,
            PullPaymentStatus = mpesaPortion <= 0 ? TandoPullPaymentStatus.NotApplicable : TandoPullPaymentStatus.Created,
            RecordedAt = DateTimeOffset.UtcNow
        };
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        if (mpesaPortion > 0)
            record = await CreateAndClaimPayout(storeId, invoiceId, record);

        return (record, null);
    }

    public async Task<TandoSplitRecord?> GetSplit(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return null;

        return GetRecord(invoice);
    }

    public async Task<(bool Success, string? Error)> MarkMpesaSettled(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (false, "invoice_not_found");

        var record = GetRecord(invoice);
        if (record is null)
            return (false, "split_not_recorded");
        if (record.MpesaSettled)
            return (true, null);

        record.MpesaSettled = true;
        record.MpesaSettledAt = DateTimeOffset.UtcNow;
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return (true, null);
    }

    // This doesnt necessarily return all splits, but only those that have been recorded in the invoice metadata. This is intentional, as we only want to return splits that have been computed and recorded.
    public async Task<IReadOnlyList<TandoSplitRecord>> ListSplits(string storeId, int skip, int take)
    {
        var invoices = await invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = new[] { storeId },
            IncludeArchived = false
        });

        return invoices
            .Select(GetRecord)
            .Where(r => r is not null)
            .Cast<TandoSplitRecord>()
            .OrderByDescending(r => r.RecordedAt)
            .Skip(skip)
            .Take(take)
            .ToList();
    }

    public async Task<(bool Success, string? Error)> UpdateMpesaPayoutStatus(
        string storeId, string invoiceId, TandoMpesaPayoutStatus status, string? payoutReference = null, string? error = null)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (false, "invoice_not_found");

        var record = GetRecord(invoice);
        if (record is null)
            return (false, "split_not_recorded");

        record.MpesaPayoutStatus = status;
        record.MpesaPayoutReference = payoutReference ?? record.MpesaPayoutReference;
        record.MpesaPayoutError = error;
        if (status == TandoMpesaPayoutStatus.Confirmed)
        {
            record.MpesaSettled = true;
            record.MpesaSettledAt = DateTimeOffset.UtcNow;
        }
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return (true, null);
    }


    private async Task<TandoSplitRecord> CreateAndClaimPayout(string storeId, string invoiceId, TandoSplitRecord record)
    {
        var settings = await subscriptionService.GetSettings();
        if (!settings.IsTreasuryConfigured)
            return record;

        if (settings.MaxPayoutAmount is { } maxPayout && record.MpesaPortionAmount > maxPayout)
        {
            record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
            record.PullPaymentError = "payout_limit_exceeded";
            await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
            return record;
        }

        var store = await storeRepository.FindStore(storeId);
        if (store is null)
        {
            record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
            record.PullPaymentError = "store_not_found";
            await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
            return record;
        }
        try
        {
            var payoutMethodId = PayoutTypes.LN.GetPayoutMethodId("BTC");
            var pullPaymentId = await pullPaymentHostedService.CreatePullPayment(store, new CreatePullPaymentRequest
            {
                Name = $"Tando split payout - invoice {invoiceId}",
                Amount = record.MpesaPortionAmount,
                Currency = record.Currency,
                PayoutMethods = [payoutMethodId.ToString()],
                AutoApproveClaims = true
            });
            var claimResult = await pullPaymentHostedService.Claim(new ClaimRequest
            {
                Destination = new LNURLPayClaimDestinaton(settings.TreasuryLightningAddress),
                PullPaymentId = pullPaymentId,
                ClaimedAmount = record.MpesaPortionAmount,
                PayoutMethodId = payoutMethodId,
                StoreId = storeId
            });
            record.PullPaymentId = pullPaymentId;
            if (claimResult.Result == ClaimRequest.ClaimResult.Ok)
            {
                record.PullPaymentStatus = TandoPullPaymentStatus.Claimed;
                record.MpesaSettled = true;
                record.MpesaSettledAt = DateTimeOffset.UtcNow;
            }
            else
            {
                record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
                record.PullPaymentError = ClaimRequest.GetErrorMessage(claimResult.Result);
            }
        }
        catch (Exception ex)
        {
            record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
            record.PullPaymentError = ex.Message;
        }
        if (record.PullPaymentStatus == TandoPullPaymentStatus.Claimed)
        {
            record = await TriggerMpesaPayout(invoiceId, storeId, record);
        }
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return record;
    }

    private async Task<TandoSplitRecord> TriggerMpesaPayout(string invoiceId, string storeId, TandoSplitRecord record)
    {
        if (record.MpesaDestinationType is null || string.IsNullOrWhiteSpace(record.MpesaDestination))
        {
            record.MpesaPayoutStatus = TandoMpesaPayoutStatus.Failed;
            record.MpesaPayoutError = "no_mpesa_destination_configured";
            return record;
        }
        try
        {
            var result = await tandoMpesaPayoutClient.TriggerPayout(new TandoPayoutRequest(
                invoiceId, storeId, record.MpesaPortionAmount, record.MpesaDestinationType.Value, record.MpesaDestination));

            record.MpesaPayoutStatus = result.Success ? TandoMpesaPayoutStatus.Triggered : TandoMpesaPayoutStatus.Failed;
            record.MpesaPayoutReference = result.PayoutReference;
            record.MpesaPayoutError = result.Error;
        }
        catch (Exception ex)
        {
            record.MpesaPayoutStatus = TandoMpesaPayoutStatus.Failed;
            record.MpesaPayoutError = ex.Message;
        }

        return record;
    }

    private static TandoSplitRecord? GetRecord(InvoiceEntity invoice) =>
        invoice.Metadata?.AdditionalData is { } data && data.TryGetValue(SplitMetadataKey, out var token)
        && token.Type == JTokenType.Object ? token.ToObject<TandoSplitRecord>() : null;
}
