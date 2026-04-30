namespace Atheres.Atlas.Domain.Constants;

public static class Roles
{
    /// <summary>Cross-tenant platform administrator — manages all companies.</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>Company administrator — manages users and settings within their company.</summary>
    public const string Admin      = "Admin";

    /// <summary>Inputs orders, views locations, and alters the schedule manually.</summary>
    public const string Logistics  = "Logistics";

    /// <summary>Driver — read-only view of their assigned route.</summary>
    public const string Driver     = "Driver";

    /// <summary>Order Importer — restricted to the CSV import interface; no other application access.</summary>
    public const string OrderImporter = "OrderImporter";

    /// <summary>Roles that a Company Admin can assign (excludes SuperAdmin and Admin).</summary>
    public static readonly IReadOnlyList<string> AdminAssignableRoles = [Logistics, Driver, OrderImporter];

    /// <summary>All roles that exist within a company tenant.</summary>
    public static readonly IReadOnlyList<string> CompanyRoles = [Admin, Logistics, Driver, OrderImporter];

    /// <summary>Every role in the system.</summary>
    public static readonly IReadOnlyList<string> All = [SuperAdmin, Admin, Logistics, Driver, OrderImporter];
}
