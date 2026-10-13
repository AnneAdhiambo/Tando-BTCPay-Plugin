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
public class TandoMerchantSettingsTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoMerchantSettingsTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin, string treasuryLightningAddress = "treasury@tando.example")
    {
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel { AppName = "Tando Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Trial Plan", Price = 0m, TrialDays = 7 });
        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id,
            TreasuryLightningAddress = treasuryLightningAddress
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


    [Fact]
    public async Task GetMpesaSettings_WhenNotConfigured_Returns404()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/mpesa");
        Assert.Equal(404, get.Status);
        var body = await ParseJson(get);
        Assert.Equal("not_configured", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SaveThenGetMpesaSettings_RoundTrips()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var save = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/mpesa",
            new APIRequestContextOptions
            {
                DataObject = new { destinationType = "MobileNumber", destination = RandomKenyanPhone(), accountNumber = (string)null }
            });
        Assert.Equal(200, save.Status);

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/mpesa");
        Assert.Equal(200, get.Status);
        var body = await ParseJson(get);
        Assert.Equal("MobileNumber", body.GetProperty("destinationType").GetString());
    }

    [Fact]
    public async Task SaveMpesaSettings_UnknownStore_ReturnsForbidden()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var save = await api.PutAsync("/plugins/api/tando/stores/does-not-exist/mpesa",
            new APIRequestContextOptions
            {
                DataObject = new { destinationType = "MobileNumber", destination = RandomKenyanPhone() }
            });
        Assert.Equal(403, save.Status);
    }

    [Fact]
    public async Task GetSplitConfig_DefaultsToZeroPercent()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/split-config");
        Assert.Equal(200, get.Status);
        var body = await ParseJson(get);
        Assert.Equal(0m, body.GetProperty("mpesaPercentage").GetDecimal());
    }

    [Fact]
    public async Task SaveSplitConfig_AboveZero_WithoutMpesaDestination_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var save = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/split-config",
            new APIRequestContextOptions { DataObject = new { mpesaPercentage = 25 } });

        Assert.Equal(400, save.Status);
        var body = await ParseJson(save);
        Assert.Equal("mpesa_destination_required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SaveSplitConfig_AboveZero_WithMpesaDestinationConfigured_Succeeds()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/mpesa",
            new APIRequestContextOptions
            {
                DataObject = new { destinationType = "MobileNumber", destination = RandomKenyanPhone() }
            });

        var save = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/split-config",
            new APIRequestContextOptions { DataObject = new { mpesaPercentage = 25 } });
        Assert.Equal(200, save.Status);

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/split-config");
        var body = await ParseJson(get);
        Assert.Equal(25m, body.GetProperty("mpesaPercentage").GetDecimal());
    }

    [Fact]
    public async Task SaveSplitConfig_OutOfRangePercentage_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var save = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/split-config",
            new APIRequestContextOptions { DataObject = new { mpesaPercentage = 150 } });

        Assert.Equal(400, save.Status);
    }

    [Fact]
    public async Task SaveSplitConfig_AboveZero_WithoutTreasury_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin, treasuryLightningAddress: null);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/mpesa",
            new APIRequestContextOptions { DataObject = new { destinationType = "MobileNumber", destination = RandomKenyanPhone() } });

        var save = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/split-config",
            new APIRequestContextOptions { DataObject = new { mpesaPercentage = 25 } });
        Assert.Equal(400, save.Status);
        Assert.Equal("treasury_not_configured", (await ParseJson(save)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task SaveSplitConfig_ZeroPercent_WithoutTreasury_Succeeds()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin, treasuryLightningAddress: null);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var save = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/split-config",
            new APIRequestContextOptions { DataObject = new { mpesaPercentage = 0 } });
        Assert.Equal(200, save.Status);
    }
}
