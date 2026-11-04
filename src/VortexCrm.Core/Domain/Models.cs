using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Domain.Ventas;

namespace VortexCrm.Core.Domain;

/// <summary>Quién opera y en qué equipo. Lo provee la app a partir de la sesión del hub.</summary>
public interface ICrmContext
{
    Guid? UserId { get; }
    string UserName { get; }

    /// <summary>Prefijo corto del equipo para numerar cotizaciones sin choques entre equipos sin conexión.</summary>
    string DeviceTag { get; }

    /// <summary>Sede que emite las cotizaciones de este equipo (puede ser null: domicilio fiscal).</summary>
    Guid? DefaultSiteId { get; }

    /// <summary>Rol efectivo del usuario en el CRM (<see cref="CrmPermissions"/>).</summary>
    string Role { get; }

    /// <summary>Si el usuario puede hacer la acción (<see cref="CrmPermissions"/>).</summary>
    bool Can(string permission);
}

/// <summary>Dato inválido o regla de negocio incumplida: se muestra tal cual al usuario.</summary>
public sealed class CrmValidationException(string message) : Exception(message);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

// ---------- Datos maestros (copia local del hub) ----------

public sealed record ItemRow(
    Guid Id, string Sku, string Name, string Kind, string? Description, string Unit, string? Barcode, string? Category,
    decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive, int Version, bool Pending,
    decimal? OnHand)
{
    /// <summary>Línea de cotización con los datos del artículo copiados (no cambia si luego se edita el artículo).</summary>
    public LineaCotizacion CrearLinea(Cotizacion cotizacion, decimal cantidad = 1) => new()
    {
        ItemId = Id,
        Descripcion = string.IsNullOrWhiteSpace(Description) ? Name : Description,
        Unidad = Unit.ToUpperInvariant(),
        Cantidad = cantidad,
        PrecioUnitario = PrecioPara(cotizacion),
    };

    /// <summary>
    /// El precio de lista (con el impuesto del país incluido, salvo en EE. UU.) expresado como lo espera la cotización:
    /// "más impuesto" lo quita y "con impuesto" lo agrega si la lista no lo trae. Si el artículo no paga impuesto, el precio
    /// es el que es.
    /// </summary>
    public decimal PrecioPara(Cotizacion cotizacion)
    {
        var regimen = cotizacion.Regimen;
        var tasa = regimen.RateOf(TaxCode);
        var decimales = VortexApi.Suite.Money.Currencies.Get(cotizacion.Moneda).Decimals;
        var precio = cotizacion.ModoIgv switch
        {
            ModoIgv.Adicional when regimen.PricesIncludeTax => Price / (1 + tasa),
            ModoIgv.Incluido when !regimen.PricesIncludeTax => Price * (1 + tasa),
            _ => Price,
        };
        return Math.Round(precio, Math.Max(2, decimales), MidpointRounding.AwayFromZero);
    }
}

public sealed record SiteRow(
    Guid Id, string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string? CountryCode,
    bool IsActive, int Version, bool Pending, int Quotes, int OpenQuotes);

/// <summary>Cliente o proveedor del hub, con la dirección completa (el CRM la imprime en las cotizaciones).</summary>
public sealed record PartyRow(
    Guid Id, string Name, string? TradeName, string Kind, string? DocumentType, string? DocumentNumber, bool IsCustomer,
    bool IsSupplier, string? Email, string? Phone, string? City, bool IsActive, int Version, bool Pending,
    string? AddressLine = null, string? District = null, string? Region = null, string? Notes = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(TradeName) ? Name : TradeName;

    public string? FullAddress =>
        string.Join(", ", new[] { AddressLine, District, City, Region }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct()) is { Length: > 0 } a
            ? a : null;
}

public sealed record CompanyInfo(
    string LegalName, string? TradeName, string? TaxId, string CountryCode, string CurrencyCode, string? Email = null,
    string? Phone = null, string? AddressLine = null, string? City = null, decimal? DefaultTaxRate = null, string? TimeZone = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(TradeName) ? LegalName : TradeName;
}

public sealed record Option(Guid Id, string Label, string? Detail = null);

public sealed record ItemDraft(
    string Sku, string Name, string Unit, string? Category, string? Barcode, decimal Price, string CurrencyCode,
    string? Description = null, bool TrackInventory = true, string Kind = "Product", string? TaxCode = "igv", bool IsActive = true);

public sealed record SiteDraft(
    string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string CountryCode, bool IsActive = true);

public sealed record PartyDraft(
    string Name, string Kind, bool IsCustomer, bool IsSupplier, string? DocumentType, string? DocumentNumber,
    string? Email, string? Phone, string? City, string CountryCode = "PE", string? AddressLine = null, string? District = null,
    string? TradeName = null, string? Notes = null);

// ---------- Vistas del CRM ----------

public sealed record OportunidadVista(Oportunidad Oportunidad, string? Cliente, string? Contacto);

public sealed record CotizacionVista(
    Guid Id, string Codigo, Guid? ClienteId, string? Cliente, EstadoCotizacion Estado, DateOnly FechaEmision, DateOnly ValidaHasta,
    decimal Total, Guid? OportunidadId, Guid? PedidoId, DateTimeOffset CreadoEn, string Moneda = "PEN")
{
    public bool Vencida(DateOnly hoy) => Estado is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada && ValidaHasta < hoy;
}

public sealed record TareaVista(Tarea Tarea, string? Cliente, string? Oportunidad);

public sealed record ContactoVista(Contacto Contacto, string? Cliente);

/// <summary>Algo que informó otro sistema sobre el cliente: una venta del POS, un comprobante o cobro del ERP, un despacho del WMS.</summary>
public sealed record ActividadRow(
    Guid Id, Guid ClienteId, string Tipo, string Titulo, string? Detalle, decimal? Monto, string? Moneda, string Origen,
    DateTimeOffset OcurridoEn);

public static class TiposActividad
{
    public const string Venta = "venta";
    public const string Comprobante = "comprobante";
    public const string Cobro = "cobro";
    public const string Despacho = "despacho";
}

/// <summary>Ajustes de ventas de la organización (se replican entre equipos).</summary>
public sealed record Ajustes(ModoIgv ModoIgvPredeterminado, string? CondicionesPredeterminadas);

/// <summary>Todo lo de un cliente en una pantalla.</summary>
public sealed record FichaCliente(
    PartyRow Cliente, IReadOnlyList<Contacto> Contactos, IReadOnlyList<OportunidadVista> Oportunidades,
    IReadOnlyList<CotizacionVista> Cotizaciones, IReadOnlyList<TareaVista> Tareas, IReadOnlyList<ActividadRow> Actividad);

public sealed record AuditRow(
    Guid Id, DateTimeOffset At, string? UserName, string? Device, string Action, string Entity, string? Reference, string? Detail);

public sealed record PanelCrm(
    ResumenPipeline Pipeline, IReadOnlyList<VentaMes> VentasPorMes, decimal GanadoEsteMes, int TareasParaHoy, int TareasVencidas,
    int CotizacionesAbiertas, decimal MontoCotizado);
