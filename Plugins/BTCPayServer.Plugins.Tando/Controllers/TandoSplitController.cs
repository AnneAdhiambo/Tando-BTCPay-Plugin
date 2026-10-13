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

    [HttpGet("~/plugins/api/tando/stores/{storeId}/splits")]
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

        if (error == "treasury_not_configured")
            return BadRequest(new { error, message = "Split payments aren't available until the Tando team sets a treasury Lightning address." });

        if (error is not null)
            return NotFound(new { error });

        return Ok(record);
    }
}
