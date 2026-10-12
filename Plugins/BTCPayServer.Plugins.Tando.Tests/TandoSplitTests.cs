using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Events;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoSplitTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoSplitTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin)
    {
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel { AppName = "Tando Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Trial Plan", Price = 0m, TrialDays = 7 });
        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id
        });
    }

    private async Task<TestAccount> NewAdminAccount()
    {
        var a = ServerTester.NewAccount();
        await a.GrantAccessAsync();
        await a.MakeAdmin();
        return a;
    }

    private async Task<string> MintApiKey(TestAccount account, string storeId = null)
    {
        var manageController = ServerTester.PayTester.GetController<UIManageController>(
            account.UserId, account.StoreId, account.IsAdmin);

        var permissionValue = new UIManageController.AddApiKeyViewModel.PermissionValueItem
        {
            Permission = Policies.CanModifyStoreSettings,
            Value = true,
            StoreMode = storeId is null
                ? UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.AllStores
                : UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.Specific,
            SpecificStores = storeId is null ? new List<string>() : new List<string> { storeId }
        };

        var label = $"tando-test-{Guid.NewGuid():N}";
        var createResult = await manageController.AddApiKey(new UIManageController.AddApiKeyViewModel
        {
            Label = label,
            PermissionValues = new List<UIManageController.AddApiKeyViewModel.PermissionValueItem> { permissionValue }
        });
        Assert.IsType<RedirectToActionResult>(createResult);

        var listResult = await manageController.APIKeys();
        var view = Assert.IsType<ViewResult>(listResult);
        var vm = Assert.IsType<UIManageController.ApiKeysViewModel>(view.Model);
        return vm.ApiKeyDatas.Single(k => k.Label == label).Id;
    }

    private async Task<IAPIRequestContext> ApiContextFor(string apiKey)
    {
        return await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = ServerUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"token {apiKey}",
                ["Content-Type"] = "application/json"
            },
            IgnoreHTTPSErrors = true
        });
    }

    private static string RandomKenyanPhone() => $"07{Random.Shared.Next(10000000, 99999999)}";

    private static async Task<JsonElement> ParseJson(IAPIResponse response)
        => JsonDocument.Parse(await response.TextAsync()).RootElement;

    private async Task<string> SignupAndGetStoreId(IAPIRequestContext api)
    {
        var response = await api.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = RandomKenyanPhone() } });
        var body = await response.TextAsync();
        Assert.True(response.Ok, $"Signup failed ({response.Status}): {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("storeId").GetString();
    }

    private async Task<string> CreateInvoice(TestAccount admin, string storeId, decimal amount)
    {
        var client = await admin.CreateClient();
        await EnsureStoreWallet(client, storeId);
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = amount,
            Currency = "KES"
        });
        return invoice.Id;
    }

    private async Task<string> CreateSettledInvoice(TestAccount admin, string storeId, decimal amount)
    {
        var invoiceId = await CreateInvoice(admin, storeId, amount);
        var client = await admin.CreateClient();
        await client.MarkInvoiceStatus(invoiceId, new MarkInvoiceStatusRequest { Status = InvoiceStatus.Settled });
        return invoiceId;
    }

    private static async Task EnableSplit(IAPIRequestContext api, string storeId, decimal mpesaPercentage)
    {
        var mpesa = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/mpesa",
            new APIRequestContextOptions { DataObject = new { destinationType = "MobileNumber", destination = RandomKenyanPhone() } });
        Assert.Equal(200, mpesa.Status);
        var split = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/split-config",
            new APIRequestContextOptions { DataObject = new { mpesaPercentage } });
        Assert.Equal(200, split.Status);
    }

    private static async Task<JsonElement> WaitForSplit(IAPIRequestContext api, string storeId, string invoiceId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var get = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
            if (get.Status == 200)
                return await ParseJson(get);
            await Task.Delay(200);
        }
        throw new Xunit.Sdk.XunitException($"No split was recorded for invoice {invoiceId} after it settled.");
    }

    [Fact]
    public async Task ComputeSplit_AtZeroPercent_DefaultsToNotTriggeredAndSettled()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);
        var invoiceId = await CreateSettledInvoice(admin, storeId, 1000m);

        // split-config defaults to 0% (no PUT called), so ComputeAndRecordSplit
        // should never reach CreateAndClaimPayout at all.
        var compute = await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, compute.Status);
        var record = await ParseJson(compute);

        Assert.Equal("NotTriggered", record.GetProperty("mpesaPayoutStatus").GetString());
        Assert.True(record.GetProperty("mpesaSettled").GetBoolean());
        Assert.Equal(0m, record.GetProperty("mpesaPortionAmount").GetDecimal());
    }

    [Fact]
    public async Task UpdateMpesaPayoutStatus_ToConfirmed_MarksRecordSettledWithReference()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);
        var invoiceId = await CreateSettledInvoice(admin, storeId, 500m);

        var compute = await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, compute.Status);

        var update = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/mpesa-payout-status",
            new APIRequestContextOptions
            {
                DataObject = new { status = "Confirmed", payoutReference = "TANDO-REF-123" }
            });
        Assert.Equal(200, update.Status);

        var get = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, get.Status);
        var record = await ParseJson(get);

        Assert.Equal("Confirmed", record.GetProperty("mpesaPayoutStatus").GetString());
        Assert.Equal("TANDO-REF-123", record.GetProperty("mpesaPayoutReference").GetString());
        Assert.True(record.GetProperty("mpesaSettled").GetBoolean());
        Assert.False(record.GetProperty("mpesaSettledAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task UpdateMpesaPayoutStatus_UnknownInvoice_ReturnsForbidden()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);

        var update = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/does-not-exist/split/mpesa-payout-status",
            new APIRequestContextOptions
            {
                DataObject = new { status = "Confirmed", payoutReference = (string)null }
            });
        Assert.Equal(403, update.Status);
    }

    [Fact]
    public async Task ListSplits_ReturnsComputedRecordsForStore()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);

        var invoiceAId = await CreateSettledInvoice(admin, storeId, 200m);
        var invoiceBId = await CreateSettledInvoice(admin, storeId, 300m);
        await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceAId}/split");
        await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceBId}/split");

        var list = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/splits");
        Assert.Equal(200, list.Status);
        var records = await ParseJson(list);

        Assert.True(records.GetArrayLength() >= 2,
            $"Expected at least 2 split records, got {records.GetArrayLength()}.");
        var totals = records.EnumerateArray().Select(r => r.GetProperty("totalAmount").GetDecimal()).ToList();
        Assert.Contains(200m, totals);
        Assert.Contains(300m, totals);
    }

    [Fact]
    public async Task ComputeSplit_OnUnsettledInvoice_IsRefusedAndCreatesNoPullPayment()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var api = await ApiContextFor(await MintApiKey(admin));

        var storeId = await SignupAndGetStoreId(api);
        await EnableSplit(api, storeId, 50m);
        var invoiceId = await CreateInvoice(admin, storeId, 1000m);

        var compute = await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(400, compute.Status);
        Assert.Equal("invoice_not_settled", (await ParseJson(compute)).GetProperty("error").GetString());

        var get = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(404, get.Status);

        var client = await admin.CreateClient();
        Assert.Empty(await client.GetPullPayments(storeId));
    }

    [Fact]
    public async Task SettlingInvoice_RecordsSplitAutomatically_Once()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var api = await ApiContextFor(await MintApiKey(admin));

        var storeId = await SignupAndGetStoreId(api);
        await EnableSplit(api, storeId, 30m);
        var invoiceId = await CreateSettledInvoice(admin, storeId, 1000m);

        var record = await WaitForSplit(api, storeId, invoiceId);
        Assert.Equal(30m, record.GetProperty("mpesaPercentage").GetDecimal());
        Assert.Equal(300m, record.GetProperty("mpesaPortionAmount").GetDecimal());
        Assert.Equal(700m, record.GetProperty("btcPortionAmount").GetDecimal());
        var recordedAt = record.GetProperty("recordedAt").GetString();

        var invoice = await ServerTester.PayTester.GetService<InvoiceRepository>().GetInvoice(invoiceId);
        ServerTester.PayTester.GetService<EventAggregator>().Publish(new InvoiceEvent(invoice, InvoiceEvent.PaidInFull));
        await Task.Delay(1000);

        var after = await WaitForSplit(api, storeId, invoiceId);
        Assert.Equal(recordedAt, after.GetProperty("recordedAt").GetString());
    }

    private readonly HashSet<string> _storesWithWallet = new();
    private async Task EnsureStoreWallet(BTCPayServerClient client, string storeId)
    {
        if (_storesWithWallet.Add(storeId))
        {
            await client.GenerateOnChainWallet(storeId, "BTC-CHAIN", new GenerateOnChainWalletRequest());
            await client.UpdateStoreRateConfiguration(storeId, new StoreRateConfiguration
            {
                IsCustomScript = true,
                EffectiveScript = "BTC_KES = 10000000;"
            });
        }
    }
}