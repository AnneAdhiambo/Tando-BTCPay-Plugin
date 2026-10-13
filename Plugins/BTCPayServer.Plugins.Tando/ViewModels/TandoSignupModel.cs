using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoSignupRequest
{
    [Required]
    public string PhoneNumber { get; set; }
    public string? DeviceName { get; set; }
}

public class TandoSignupResponse
{
    public string StoreId { get; set; }
    public string PhoneNumber { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? PosAppId { get; set; }
    public string? CartAppId { get; set; }
    public string UserId { get; set; }
    public string ApiKey { get; set; }
}

public class TandoIssueAppKeyRequest
{
    [Required]
    public string PhoneNumber { get; set; }
    public string? DeviceName { get; set; }
}

public class TandoIssueAppKeyResponse
{
    public string UserId { get; set; }
    public string PhoneNumber { get; set; }
    public string ApiKey { get; set; }
    public TandoStoreAccess[] Stores { get; set; }
}

public class TandoStoreAccess
{
    public string StoreId { get; set; }
    public string StoreName { get; set; }
    public string? Role { get; set; }
}