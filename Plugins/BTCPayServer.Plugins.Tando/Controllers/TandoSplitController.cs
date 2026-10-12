using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoSplitController(TandoSplitService splitService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Get(string storeId, string invoiceId)
    {
        var record = await splitService.GetSplit(storeId, invoiceId);
        if (record is null)
            return NotFound(new { error = "split_not_recorded" });

        return Ok(record);
    }

    [HttpGet("list")]
    public async Task<IActionResult> List(string storeId, int skip = 0, int take = 50)
    {
        var records = await splitService.ListSplits(storeId, skip, take);
        return Ok(records);
    }

    [HttpPost]
    public async Task<IActionResult> Compute(string storeId, string invoiceId)
    {
        var (record, error) = await splitService.ComputeAndRecordSplit(storeId, invoiceId);

        if (error == "invoice_not_settled")
            return BadRequest(new { error, message = "Only a settled invoice can be split." });

        if (error is not null)
            return NotFound(new { error });

        return Ok(record);
    }

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
