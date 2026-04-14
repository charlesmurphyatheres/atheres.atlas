using System.ComponentModel.DataAnnotations;

namespace Atheres.Atlas.Domain.DTOs.Auth;

public class RefreshTokenDto
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}
