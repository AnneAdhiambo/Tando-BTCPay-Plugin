using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split")]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoSplitOperatorController(TandoSplitService splitService) : Controller
{

    [HttpPut("settle")]
    public async Task<IActionResult> Settle(string storeId, string invoiceId)
    {
        var (success, error) = await splitService.MarkMpesaSettled(storeId, invoiceId);
        if (!success)
            return error == "invoice_not_found" ? NotFound(new { error }) : BadRequest(new { error });

        return Ok();
    }

    [HttpPut("mpesa-payout-status")]
    public async Task<IActionResult> UpdateMpesaPayoutStatus(string storeId, string invoiceId,
    [FromBody] TandoUpdateMpesaPayoutStatusRequest request)
    {
        var response = await splitService.UpdateMpesaPayoutStatus(storeId, invoiceId, request.Status, request.PayoutReference, request.Error);
        if (!response.Success)
            return response.Error == "invoice_not_found" ? NotFound(new { response.Error }) : BadRequest(new { response.Error });

        return Ok();
    }
}