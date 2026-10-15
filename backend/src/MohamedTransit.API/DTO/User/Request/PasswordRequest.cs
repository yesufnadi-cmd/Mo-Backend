namespace MohamedTransit.API.DTO.User.Request;

public class PasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
