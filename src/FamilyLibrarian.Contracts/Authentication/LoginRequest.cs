using System.ComponentModel.DataAnnotations;

namespace FamilyLibrarian.Contracts.Authentication;

public sealed class LoginRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
}
