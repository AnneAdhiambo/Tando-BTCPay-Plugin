using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}/lightning")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoLightningController(StoreRepository storeRepository, TandoLightningProvisionerFactory lightningProvisionerFactory) : Controller
{
    [HttpPut("connect")]
    public async Task<IActionResult> ConnectLightning(string storeId, [FromBody] TandoConnectLightningRequest request)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        string connectionString;
        if (!string.IsNullOrWhiteSpace(request?.ConnectionString))
        {
            connectionString = request.ConnectionString;
        }
        else if (request?.LightningProvision is not null)
        {
            var provisioner = lightningProvisionerFactory.Get(request.LightningProvision.ProviderType);
            if (provisioner is null)
                return BadRequest(new { error = "unsupported_provider_type" });

            var result = await provisioner.Provision(request.LightningProvision);
            if (!result.IsSuccess)
                return BadRequest(new { error = result.Error });

            connectionString = result.ConnectionString!;
        }
        else
        {
            return BadRequest(new { error = "connection_string_required" });
        }
        var ownedStores = await storeRepository.GetStoresByUserId(User.GetId());
        // 404, not 403: don't reveal to a non-owner whether storeId exists at all.
        var ownedStore = ownedStores.FirstOrDefault(s => s.Id == storeId);
        if (ownedStore is null)
            return NotFound(new { error = "store_not_found" });

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var config = new LightningPaymentMethodConfig { ConnectionString = connectionString };
        ownedStore.SetPaymentMethodConfig(paymentMethodId, JToken.FromObject(config));
        var blob = ownedStore.GetStoreBlob();
        blob.SetExcluded(paymentMethodId, false);
        ownedStore.SetStoreBlob(blob);
        await storeRepository.UpdateStore(ownedStore);
        return Ok(new { storeId, paymentMethodId = paymentMethodId.ToString() });
    }
}