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
public class TandoProductsTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoProductsTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
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


    [Fact]
    public async Task Create_WithValidNameAndPrice_Succeeds()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var create = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Chai", price = 50m } });

        Assert.Equal(200, create.Status);
        var body = await ParseJson(create);
        Assert.Equal("Chai", body.GetProperty("name").GetString());
        Assert.Equal(50m, body.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Create_WithoutName_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var create = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "", price = 50m } });

        Assert.Equal(400, create.Status);
        var body = await ParseJson(create);
        Assert.Equal("name_required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_WithNegativePrice_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var create = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Chai", price = -5m } });

        Assert.Equal(400, create.Status);
        var body = await ParseJson(create);
        Assert.Equal("invalid_price", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task List_ReturnsCreatedProducts()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Chai", price = 50m } });
        await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Mandazi", price = 20m } });

        var list = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/products");
        Assert.Equal(200, list.Status);
        var records = await ParseJson(list);
        var names = records.EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Contains("Chai", names);
        Assert.Contains("Mandazi", names);
    }

    [Fact]
    public async Task Delete_ExistingProduct_RemovesIt()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var create = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Chai", price = 50m } });
        var itemId = (await ParseJson(create)).GetProperty("id").GetString();

        var delete = await merchantApi.DeleteAsync($"/plugins/api/tando/stores/{storeId}/products/{itemId}");
        Assert.Equal(200, delete.Status);

        var list = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/products");
        var records = await ParseJson(list);
        var ids = records.EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain(itemId, ids);
    }

    [Fact]
    public async Task Delete_UnknownProduct_ReturnsNotFound()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var delete = await merchantApi.DeleteAsync($"/plugins/api/tando/stores/{storeId}/products/does-not-exist");
        Assert.Equal(404, delete.Status);
        var body = await ParseJson(delete);
        Assert.Equal("product_not_found", body.GetProperty("error").GetString());
    }
}
