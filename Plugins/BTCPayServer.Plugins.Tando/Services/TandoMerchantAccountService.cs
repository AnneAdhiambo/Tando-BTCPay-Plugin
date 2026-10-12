using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Security.Greenfield;
using Microsoft.AspNetCore.Identity;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoMerchantAccountService(UserManager<ApplicationUser> userManager, APIKeyRepository apiKeyRepository)
{
    private const string SyntheticEmailDomain = "bitcoin.co.ke";
    private const string AppKeyLabelPrefix = "tando-app";

    private static readonly string[] AppKeyPermissions =
    {
        Policies.CanModifyStoreSettings,
        Policies.CanViewProfile
    };
    private static string RandomPassword() => Encoders.Base58.EncodeData(RandomUtils.GetBytes(32)) + "aA1!";

    public static string SyntheticEmail(string normalizedPhone) => $"{normalizedPhone}@{SyntheticEmailDomain}";

    public Task<ApplicationUser?> FindUser(string normalizedPhone) => userManager.FindByEmailAsync(SyntheticEmail(normalizedPhone));

    public async Task<(ApplicationUser? User, bool AlreadyExisted, string[] Errors)> GetOrCreateUser(string normalizedPhone)
    {
        var email = SyntheticEmail(normalizedPhone);
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            if (user.PasswordHash is null)
                await userManager.AddPasswordAsync(user, RandomPassword());
            return (user, true, Array.Empty<string>());
        }

        user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            RequiresEmailConfirmation = false,
            RequiresApproval = false,
            Approved = true,
            Created = DateTimeOffset.UtcNow
        };
        var blob = user.GetBlob() ?? new UserBlob();
        blob.Name = normalizedPhone;
        user.SetBlob(blob);
        var result = await userManager.CreateAsync(user, RandomPassword());
        return result.Succeeded ? (user, false, Array.Empty<string>()) : (null, false, result.Errors.Select(e => e.Description).ToArray());
    }

    public async Task<string> CreateAppKey(string userId, string? deviceName)
    {
        var key = new APIKeyData
        {
            Id = Encoders.Hex.EncodeData(RandomUtils.GetBytes(20)),
            Type = APIKeyType.Permanent,
            UserId = userId,
            Label = $"{AppKeyLabelPrefix}:{(string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim())}"
        };
        key.SetBlob(new APIKeyBlob { Permissions = AppKeyPermissions });
        await apiKeyRepository.CreateKey(key);
        return key.Id;
    }

    public async Task RevokeAppKeys(string userId)
    {
        var keys = await apiKeyRepository.GetKeys(new APIKeyRepository.APIKeyQuery { UserId = new[] { userId } });
        foreach (var key in keys.Where(k => k.Label?.StartsWith(AppKeyLabelPrefix, StringComparison.Ordinal) is true))
            await apiKeyRepository.Remove(key.Id, userId);
    }
}