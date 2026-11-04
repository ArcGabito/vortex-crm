using VortexApi.Suite.Security;

namespace VortexCrm.Core.Domain;

/// <summary>
/// Qué puede hacer cada rol del CRM. El OrgAdmin asigna el rol en el hub (Sistemas → CRM → miembros); quien no tiene rol
/// entra como <c>vendedor</c>. Un vendedor trabaja sus propias oportunidades y cotizaciones; el gerente, las de todo el
/// equipo. Se valida en los servicios de Core, no solo en la interfaz.
/// </summary>
public static class CrmPermissions
{
    /// <summary>Crear y trabajar oportunidades, cotizaciones, contactos y tareas propias.</summary>
    public const string Sell = "sales.work";

    /// <summary>Editar o cambiar de etapa lo que es de otro vendedor.</summary>
    public const string EditAny = "sales.edit_any";

    /// <summary>Eliminar oportunidades, contactos y tareas (los borradores de cotización propios se pueden borrar siempre).</summary>
    public const string Delete = "sales.delete";

    public const string CreateCustomer = "customer.create";

    /// <summary>Crear o editar productos y sedes (datos maestros de la suite).</summary>
    public const string EditCatalog = "catalog.edit";

    /// <summary>Ajustes de ventas de la organización (IGV y condiciones por defecto).</summary>
    public const string EditSettings = "settings.edit";

    public const string ViewAudit = "audit.view";

    public const string Seller = "vendedor";
    public const string SalesManager = "gerente_ventas";
    public const string ReadOnly = "consulta";

    public static readonly PermissionMatrix Matrix = PermissionMatrix.Create(Seller,
        (ReadOnly, []),
        (Seller, [Sell, CreateCustomer]),
        (SalesManager, [Sell, CreateCustomer, EditAny, Delete, EditSettings, ViewAudit]));

    public static string Describe(string permission) => permission switch
    {
        Sell => "registrar ventas",
        EditAny => "editar lo que es de otro vendedor",
        Delete => "eliminar registros",
        CreateCustomer => "registrar clientes",
        EditCatalog => "editar productos y sedes",
        EditSettings => "cambiar los ajustes de ventas",
        ViewAudit => "ver la auditoría",
        _ => permission,
    };
}
