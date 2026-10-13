using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoSecurityTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoSecurityTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin)
    {
        var client = await admin.CreateClient();

        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel
        {
            AppName = "Tando Test Offering"
        });

        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new()
        {
            Name = "Trial Plan",
            Price = 0m,
            TrialDays = 7
        });

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
        var created = vm.ApiKeyDatas.Single(k => k.Label == label);
        var savedPermissions = created.GetBlob().Permissions;
        Console.WriteLine($"[MintApiKey] storeId={storeId ?? "(admin)"} savedPermissions=[{string.Join(", ", savedPermissions)}]");
        return created.Id;
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

    [Fact]
    public async Task TandoSignup_ThenAddProduct_RoundTrips()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var create = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Chapati", price = 20 } });
        Assert.Equal(200, create.Status);
        var productId = (await ParseJson(create)).GetProperty("id").GetString();

        var list = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/products");
        var products = await ParseJson(list);
        Assert.Contains(products.EnumerateArray(), p => p.GetProperty("id").GetString() == productId);
        await api.DisposeAsync();
        await merchantApi.DisposeAsync();
    }

    [Fact]
    public async Task MerchantKey_CannotAccessAnotherMerchantsStore()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var backendApi = await ApiContextFor(adminKey);

        var (storeAId, merchantAApi) = await SignupMerchant(backendApi);
        var (storeBId, merchantBApi) = await SignupMerchant(backendApi);

        var ownStore = await merchantAApi.GetAsync($"/plugins/api/tando/stores/{storeAId}/products");
        Assert.Equal(200, ownStore.Status);

        var otherStore = await merchantAApi.GetAsync($"/plugins/api/tando/stores/{storeBId}/products");
        Assert.Equal(403, otherStore.Status);

        await backendApi.DisposeAsync();
        await merchantAApi.DisposeAsync();
        await merchantBApi.DisposeAsync();
    }

    [Fact]
    public async Task BackendKey_IsNotAMemberOfMerchantStores()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var backendApi = await ApiContextFor(adminKey);
        var (storeId, _) = await SignupMerchant(backendApi);

        var create = await backendApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Chapati", price = 20 } });
        Assert.Equal(403, create.Status);
    }

    [Fact]
    public async Task MerchantKey_CannotReportItsOwnMpesaPayout()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var backendApi = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(backendApi);

        var update = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/any/split/mpesa-payout-status",
            new APIRequestContextOptions { DataObject = new { status = "Confirmed", payoutReference = "FAKE" } });
        Assert.Equal(403, update.Status);
    }
}
