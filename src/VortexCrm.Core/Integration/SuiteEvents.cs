using System.Text.Json;

namespace VortexCrm.Core.Integration;

// Contratos de los eventos de la suite Vortex, versión 1 (docs/SISTEMAS.md del hub, sección 6).
// Se serializan con VortexJson.Options (camelCase). Se pueden agregar campos, nunca quitar ni cambiar su significado;
// al leer, los campos desconocidos se ignoran.

public static class SuiteEvents
{
    // Publica el CRM
    public const string QuoteAccepted = "quote.accepted";
    public const string DealWon = "deal.won";
    public const string OrderCreated = "order.created";

    // Replicación de los datos propios del CRM entre los equipos de la organización (catálogo del hub)
    public const string RecordUpserted = "crm.record.upserted";
    public const string RecordDeleted = "crm.record.deleted";

    // Consume el CRM (actividad del cliente)
    public const string SaleCompleted = "sale.completed";
    public const string InvoiceIssued = "invoice.issued";
    public const string PaymentReceived = "payment.received";
    public const string InvoiceVoided = "invoice.voided";
    public const string ShipmentDispatched = "shipment.dispatched";
}

// ---------- Publicados por el CRM ----------

public sealed record OrderLine(Guid ItemId, decimal Quantity, decimal UnitPrice);

/// <summary><c>quote.accepted</c>. Extiende v1 con <c>number</c>. Solo las líneas con artículo del catálogo.</summary>
public sealed record QuoteAccepted(
    Guid QuoteId, string? Number, Guid CustomerId, IReadOnlyList<OrderLine> Lines, decimal Total, string CurrencyCode);

/// <summary><c>deal.won</c>. Extiende v1 con <c>pipelineId</c> y <c>lines</c> (los productos de la oportunidad, si los tiene).</summary>
public sealed record DealWon(Guid DealId, Guid CustomerId, decimal Amount, string CurrencyCode, Guid? QuoteId, Guid? PipelineId = null,
    IReadOnlyList<DealLine>? Lines = null);

public sealed record DealLine(Guid? ItemId, string Description, decimal Quantity, decimal UnitPrice);

/// <summary><c>order.created</c>. Extiende v1 con <c>quoteId</c>; los precios incluyen IGV como el precio de lista.</summary>
public sealed record OrderCreated(
    Guid OrderId, string? Number, Guid CustomerId, Guid? SiteId, IReadOnlyList<OrderLine> Lines, decimal Total, string CurrencyCode,
    Guid? QuoteId);

/// <summary>
/// <c>crm.record.upserted</c>: la versión completa de un registro propio del CRM. Los demás equipos la aplican si es
/// más nueva que la suya (<c>updatedAt</c>), así el último cambio gana.
/// </summary>
public sealed record CrmRecord(string Kind, Guid Id, DateTimeOffset UpdatedAt, JsonElement? Data = null);

public static class CrmRecordKinds
{
    public const string Contacto = "contacto";
    public const string Oportunidad = "oportunidad";
    public const string Cotizacion = "cotizacion";
    public const string Tarea = "tarea";
    public const string Ajustes = "ajustes";
    public const string Embudo = "embudo";
    public const string Meta = "meta";
    public const string Lead = "lead";
}

// ---------- Consumidos por el CRM ----------

public sealed record SaleCompleted(Guid SaleId, string? Number, Guid? CustomerId, string? CurrencyCode, decimal Total);

public sealed record InvoiceIssued(Guid InvoiceId, string? Number, Guid? CustomerId, decimal Total, string? CurrencyCode, string? Kind);

public sealed record PaymentReceived(Guid PaymentId, Guid? CustomerId, decimal Amount, string? CurrencyCode, string? Method, string? Number);

/// <summary><c>invoice.voided</c> del ERP: comprobante anulado; <c>customerId</c> desde que el ERP lo incluye.</summary>
public sealed record InvoiceVoided(Guid InvoiceId, string? Number, string? Reason, Guid? CustomerId);

public sealed record ShipmentDispatched(
    Guid ShipmentId, Guid OrderId, Guid? CustomerId, string? Carrier, string? TrackingNumber, string? Number);
