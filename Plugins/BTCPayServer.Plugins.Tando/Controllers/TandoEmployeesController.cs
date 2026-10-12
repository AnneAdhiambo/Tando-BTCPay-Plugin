using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Helper;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static BTCPayServer.Services.Stores.StoreRepository;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}/employees")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoEmployeesController(StoreRepository storeRepository, TandoMerchantAccountService accountService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> List(string storeId)
    {
        var users = await storeRepository.GetStoreUsers(storeId, new[] { StoreRoleId.Employee });
        return Ok(users.Select(u => new
        {
            userId = u.Id,
            email = u.Email,
            phoneNumber = u.UserBlob?.Name
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Invite(string storeId, [FromBody] TandoInviteEmployeeRequest request)
    {
        var normalizedPhone = KenyanPhoneNumber.Normalize(request?.PhoneNumber);
        if (normalizedPhone is null)
            return BadRequest(new { error = "invalid_phone_number", detail = $"Expected a Kenyan MSISDN, e.g. {KenyanPhoneNumber.ExampleFormat}." });

        var (user, alreadyExisted, errors) = await accountService.GetOrCreateUser(normalizedPhone);
        if (user is null)
            return BadRequest(new { error = "employee_creation_failed", detail = errors });

        var result = await storeRepository.AddOrUpdateStoreUser(storeId, user.Id, StoreRoleId.Employee);
        if (result is not AddOrUpdateStoreUserResult.Success)
        {
            return result switch
            {
                AddOrUpdateStoreUserResult.DuplicateRole => Ok(new TandoEmployeeResponse { UserId = user.Id, PhoneNumber = normalizedPhone, AlreadyExisted = true }),
                _ => StatusCode(500, new { error = "employee_role_assignment_failed" })
            };
        }
        return Ok(new TandoEmployeeResponse { UserId = user.Id, PhoneNumber = normalizedPhone, AlreadyExisted = alreadyExisted });
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> Revoke(string storeId, string userId)
    {
        var employees = await storeRepository.GetStoreUsers(storeId, new[] { StoreRoleId.Employee });
        if (employees.All(u => u.Id != userId))
            return BadRequest(new { error = "cannot_remove_last_owner_or_user_not_found" });

        var removed = await storeRepository.RemoveStoreUser(storeId, userId);
        if (!removed)
            return BadRequest(new { error = "cannot_remove_last_owner_or_user_not_found" });

        if ((await storeRepository.GetStoresByUserId(userId)).Length == 0)
            await accountService.RevokeAppKeys(userId);

        return Ok();
    }
}

