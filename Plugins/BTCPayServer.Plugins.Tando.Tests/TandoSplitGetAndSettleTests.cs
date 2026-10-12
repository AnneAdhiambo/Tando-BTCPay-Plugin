using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoSplitGetAndSettleTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoSplitGetAndSettleTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
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
            PermissionValues = storeId is null
                ? new List<UIManageController.AddApiKeyViewModel.PermissionValueItem>
                {
                    permissionValue,
                    new() { Permission = Policies.CanModifyServerSettings, Value = true }
                }
                : new List<UIManageController.AddApiKeyViewModel.PermissionValueItem> { permissionValue }
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
    private readonly Dictionary<string, string> _merchantKeys = new();

    private async Task<(string StoreId, IAPIRequestContext Api)> SignupMerchant(IAPIRequestContext backendApi, string phone = null)
    {
        var response = await backendApi.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone ?? RandomKenyanPhone() } });
        var body = await response.TextAsync();
        Assert.True(response.Ok, $"Signup failed ({response.Status}): {body}");
        var root = JsonDocument.Parse(body).RootElement;
        var storeId = root.GetProperty("storeId").GetString();
        var apiKey = root.GetProperty("apiKey").GetString();
        _merchantKeys[storeId] = apiKey;
        return (storeId, await ApiContextFor(apiKey));
    }

    private BTCPayServerClient MerchantClient(string storeId) => new(ServerUri, _merchantKeys[storeId]);

    private async Task<string> CreateInvoice(TestAccount admin, string storeId, decimal amount)
    {
        var client = MerchantClient(storeId);
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
        var client = MerchantClient(storeId);
        await client.MarkInvoiceStatus(invoiceId, new MarkInvoiceStatusRequest { Status = InvoiceStatus.Settled });
        return invoiceId;
    }

    [Fact]
    public async Task Get_BeforeSplitComputed_Returns404()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);
        var invoiceId = await CreateInvoice(admin, storeId, 100m);

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(404, get.Status);
        var body = await ParseJson(get);
        Assert.Equal("split_not_recorded", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_AfterSplitComputed_ReturnsRecord()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);
        var invoiceId = await CreateSettledInvoice(admin, storeId, 400m);

        await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, get.Status);
        var body = await ParseJson(get);
        Assert.Equal(400m, body.GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task Settle_UnknownInvoice_ReturnsNotFound()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, _) = await SignupMerchant(api);
        var settle = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/does-not-exist/split/settle");
        Assert.Equal(404, settle.Status);
    }

    [Fact]
    public async Task Settle_ComputedSplit_Succeeds()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);
        var invoiceId = await CreateSettledInvoice(admin, storeId, 150m);

        var compute = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, compute.Status);

        var settle = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split/settle");
        Assert.Equal(200, settle.Status);
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
