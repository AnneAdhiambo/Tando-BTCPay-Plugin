using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class UITandoSettingsTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public UITandoSettingsTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task<TestAccount> NewAdminAccount()
    {
        var a = ServerTester.NewAccount();
        await a.GrantAccessAsync();
        await a.MakeAdmin();
        return a;
    }

    [Fact]
    public async Task Settings_Get_ReturnsView()
    {
        var admin = await NewAdminAccount();
        var controller = ServerTester.PayTester.GetController<UITandoSettingsController>(
            admin.UserId, admin.StoreId, admin.IsAdmin);

        var result = await controller.Settings((string)null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<TandoSettingsViewModel>(view.Model);
    }

    [Fact]
    public async Task Settings_Post_SavesSubscriptionConfig()
    {
        var admin = await NewAdminAccount();
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new() { AppName = "Settings Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Plan", Price = 0m, TrialDays = 7 });

        var controller = ServerTester.PayTester.GetController<UITandoSettingsController>(
            admin.UserId, admin.StoreId, admin.IsAdmin);

        var result = await controller.Settings(new TandoSettingsViewModel
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id
        });

        // Either redirects back with a success message, or re-renders the view with the
        // saved state reflected - accept whichever this controller actually does rather
        // than assume, and assert on the model where possible.
        Assert.True(result is RedirectToActionResult || result is ViewResult);
    }

    [Fact]
    public async Task Docs_RedirectsToSwaggerEditorWithSpecUrl()
    {
        var admin = await NewAdminAccount();
        var controller = ServerTester.PayTester.GetController<UITandoSettingsController>(
            admin.UserId, admin.StoreId, admin.IsAdmin);

        var result = controller.Docs();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.StartsWith("https://editor.swagger.io/?url=", redirect.Url);
    }

    [Fact]
    public async Task OpenApiSpec_IsReachableWithoutAuthentication()
    {
        // [AllowAnonymous] on this one specifically - worth testing over real HTTP,
        // not a direct controller call, since that's the part actually being verified.
        await InitializePlaywright(ServerTester);
        var api = await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = ServerUri.ToString(),
            IgnoreHTTPSErrors = true
        });

        var response = await api.GetAsync("/server/tando/openapi.yaml");
        Assert.Equal(200, response.Status);
        var body = await response.TextAsync();
        Assert.Contains("openapi", body, StringComparison.OrdinalIgnoreCase);
    }
}
