using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Helper;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/")]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoOnboardingController(StoreRepository storeRepository, TandoSubscriptionService subscriptionService, 
    TandoProductProvisioningService productProvisioningService, TandoMerchantAccountService accountService) : Controller
{
    private const string PreferredRateSource = "bitcoinkenya";
    private const string DefaultCurrency = "KES";
    private const string PhoneMetadataKey = "tandoPhoneNumber";
    private const string PlanMetadataKey = "tandoSubscriptionPlanId";

    [HttpGet("subscription/status")]
    public async Task<IActionResult> SubscriptionStatus([FromQuery] string phoneNumber)
    {
        var normalizedPhone = KenyanPhoneNumber.Normalize(phoneNumber);
        if (normalizedPhone == null)
            return BadRequest(new { error = "invalid_phone_number", detail = "Expected a Kenyan MSISDN, e.g. 0712345678 or +254712345678." });

        var status = await subscriptionService.GetStatus(normalizedPhone);
        return Ok(status);
    }

    [HttpGet("subscription/plans")]
    public async Task<IActionResult> SubscriptionPlans()
    {
        var plans = await subscriptionService.GetAvailablePlans();
        if (plans is null)
            return Ok(new { configured = false, plans = Array.Empty<TandoPlan>() });

        return Ok(new { configured = true, plans });
    }

    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] TandoSignupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.PhoneNumber))
            return BadRequest(new { error = "phone_number_required" });

        var normalizedPhone = KenyanPhoneNumber.Normalize(request.PhoneNumber);
        if (normalizedPhone is null)
            return BadRequest(new { error = "invalid_phone_number", detail = "Expected a Kenyan MSISDN, e.g. 0712345678 or +254712345678." });

        var status = await subscriptionService.GetStatus(normalizedPhone);
        if (!status.Configured)
        {
            return StatusCode(503, new
            {
                error = "subscription_not_configured",
                message = "Subscriptions aren't set up yet. Please contact the Tando team before trying to sign up."
            });
        }
        if (!status.Active)
        {
            try
            {
                status = await subscriptionService.CreateFreeTrialSubscriber(normalizedPhone, Request.GetRequestBaseUrl(), cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = "subscription_not_configured", message = ex.Message });
            }

            if (!status.Active)
                return StatusCode(500, new { error = "subscriber_creation_failed" });
        }
        var (merchant, _, errors) = await accountService.GetOrCreateUser(normalizedPhone);
        if (merchant is null)
            return BadRequest(new { error = "merchant_user_creation_failed", detail = errors });

        var existingStore = await FindOwnedStore(merchant.Id, normalizedPhone);


        if (existingStore is not null)
        {
            await RefreshPlanMetadata(existingStore, status.PlanId);
            var (posAppId, cartAppId) = await productProvisioningService.ProvisionDefaultApps(existingStore);
            return Ok(new TandoSignupResponse
            {
                StoreId = existingStore.Id,
                PhoneNumber = normalizedPhone,
                AlreadyExisted = true,
                PosAppId = posAppId,
                CartAppId = cartAppId,
                UserId = merchant.Id,
                ApiKey = await accountService.CreateAppKey(merchant.Id, request.DeviceName),
            });
        }
        var store = await storeRepository.GetDefaultStoreTemplate();
        store.StoreName = normalizedPhone;
        var blob = store.GetStoreBlob();
        blob.DefaultCurrency = DefaultCurrency;
        var rate = blob.GetOrCreateRateSettings(false);
        rate.PreferredExchange = PreferredRateSource;
        rate.RateScripting = false;
        blob.AdditionalData[PhoneMetadataKey] = normalizedPhone;
        blob.AdditionalData[PlanMetadataKey] = status.PlanId;
        store.SetStoreBlob(blob);
        var result = await storeRepository.CreateStore(merchant.Id, store);
        if (result != StoreRepository.CreateStoreResult.Created)
            return BadRequest(new { error = "store_creation_failed", detail = result.ToString() });

        var (newPosAppId, newCartAppId) = await productProvisioningService.ProvisionDefaultApps(store);
        return Ok(new TandoSignupResponse
        {
            StoreId = store.Id,
            PhoneNumber = normalizedPhone,
            AlreadyExisted = false,
            PosAppId = newPosAppId,
            CartAppId = newCartAppId,
            UserId = merchant.Id,
            ApiKey = await accountService.CreateAppKey(merchant.Id, request.DeviceName)
        });
    }

    [HttpPost("users/api-keys")]
    public async Task<IActionResult> IssueAppKey([FromBody] TandoIssueAppKeyRequest request)
    {
        var normalizedPhone = KenyanPhoneNumber.Normalize(request?.PhoneNumber);
        if (normalizedPhone is null)
            return BadRequest(new { error = "invalid_phone_number", detail = $"Expected a Kenyan MSISDN, e.g. {KenyanPhoneNumber.ExampleFormat}." });

        var user = await accountService.FindUser(normalizedPhone);
        var stores = user is null ? Array.Empty<StoreData>() : await storeRepository.GetStoresByUserId(user.Id);
        if (user is null || stores.Length == 0)
            return NotFound(new { error = "no_store_access" });

        return Ok(new TandoIssueAppKeyResponse
        {
            UserId = user.Id,
            PhoneNumber = normalizedPhone,
            ApiKey = await accountService.CreateAppKey(user.Id, request!.DeviceName),
            Stores = stores.Select(s => new TandoStoreAccess
            {
                StoreId = s.Id,
                StoreName = s.StoreName,
                Role = s.GetStoreRoleOfUser(user.Id)?.Role
            }).ToArray()
        });
    }

    private async Task<StoreData?> FindOwnedStore(string merchantId, string normalizedPhone)
    {
        var stores = await storeRepository.GetStoresByUserId(merchantId);
        return stores.FirstOrDefault(s =>
            s.GetStoreRoleOfUser(merchantId)?.Role == StoreRoleId.Owner.Role &&
            s.GetStoreBlob().AdditionalData.TryGetValue(PhoneMetadataKey, out var phone) &&
            phone?.ToString() == normalizedPhone);
    }



    private async Task RefreshPlanMetadata(StoreData store, string? currentPlanId)
    {
        var blob = store.GetStoreBlob();
        if (blob.AdditionalData[PlanMetadataKey]?.ToString() == currentPlanId) return; 

        blob.AdditionalData[PlanMetadataKey] = currentPlanId;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
    }
}