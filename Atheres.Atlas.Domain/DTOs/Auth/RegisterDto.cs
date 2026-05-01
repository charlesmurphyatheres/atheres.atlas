using System.ComponentModel.DataAnnotations;

namespace Atheres.Atlas.Domain.DTOs.Auth;

public class RegisterDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>Role to assign — defaults to Driver if omitted. Admin only.</summary>
    public string? Role { get; set; }

    /// <summary>
    /// Company to register the user under.
    /// Required for all roles except SuperAdmin.
    /// Company Admins can only add users to their own company.
    /// SuperAdmin can specify any company.
    /// </summary>
    public Guid? CompanyId { get; set; }

    /// <summary>Warehouse to pin the user to. Required when Role is
    /// OrderImporter — every order they create or see is filtered to this
    /// warehouse. Ignored for other roles.</summary>
    public Guid? WarehouseId { get; set; }
}
