using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Pdf;
using VortexCrm.Core.Sync;

namespace VortexCrm.Core.Services;

/// <summary>
/// Cotizaciones con IGV. Al aceptarse publican <c>quote.accepted</c> (y <c>deal.won</c> si ganan su oportunidad); desde
/// una aceptada se genera el pedido (<c>order.created</c>) con el mismo Id que calcularía el ERP, así nunca se duplica.
/// </summary>
public sealed class ServicioCotizaciones(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    /// <summary>Id del pedido que sale de una cotización. El ERP usa el mismo cálculo (<c>OrderService.OrderIdForQuote</c>).</summary>
    public static Guid PedidoIdPara(Guid cotizacionId) => DeterministicId.From("order_from_quote", cotizacionId);

    public async Task<IReadOnlyList<CotizacionVista>> ListarAsync(string? busqueda = null, EstadoCotizacion? estado = null,
        Guid? clienteId = null, Guid? oportunidadId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT q.*, p.name AS cliente,
                   (SELECT COALESCE(SUM(ROUND(l.cantidad * l.precio_unitario, 2)), 0) FROM cotizacion_lineas l WHERE l.cotizacion_id = q.id) AS suma
            FROM cotizaciones q LEFT JOIN parties p ON p.id = q.cliente_id
            WHERE ($estado IS NULL OR q.estado = $estado) AND ($cliente IS NULL OR q.cliente_id = $cliente)
              AND ($oportunidad IS NULL OR q.oportunidad_id = $oportunidad)
              AND ($q IS NULL OR q.numero LIKE $like OR p.name LIKE $like OR p.document_number LIKE $like)
            ORDER BY q.creado_en DESC
            """, r =>
            {
                var modo = Enum.Parse<ModoIgv>(r.Str("modo_igv"));
                var suma = r.Dec("suma");
                var total = modo == ModoIgv.Adicional ? suma + Math.Round(suma * Impuestos.TasaIgv, 2, MidpointRounding.AwayFromZero) : suma;
                return new CotizacionVista(r.Guid("id"), r.StrOrNull("numero") ?? "Nueva", r.GuidOrNull("cliente_id"), r.StrOrNull("cliente"),
                    Enum.Parse<EstadoCotizacion>(r.Str("estado")), DateOnly.Parse(r.Str("fecha_emision"), System.Globalization.CultureInfo.InvariantCulture),
                    DateOnly.Parse(r.Str("valida_hasta"), System.Globalization.CultureInfo.InvariantCulture), total, r.GuidOrNull("oportunidad_id"),
                    r.GuidOrNull("pedido_id"), r.Time("creado_en"));
            }, null, ("$estado", estado?.ToString()), ("$cliente", clienteId), ("$oportunidad", oportunidadId), ("$q", Clean(busqueda)),
            ("$like", Like(busqueda)));
    }

    public async Task<Cotizacion?> ObtenerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ObtenerAsync(c, null, id);
    }

    /// <summary>Una cotización nueva con los ajustes de la organización (IGV, condiciones, validez) y la sede de este equipo.</summary>
    public async Task<Cotizacion> NuevaAsync(Guid? clienteId = null, Guid? oportunidadId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var ajustes = await ServicioClientes.AjustesAsync(c);
        var hoy = FormatoPeru.Hoy(Clock);
        var cotizacion = new Cotizacion
        {
            ClienteId = clienteId, OportunidadId = oportunidadId, SedeId = DefaultSite, ModoIgv = ajustes.ModoIgvPredeterminado,
            Condiciones = ajustes.CondicionesPredeterminadas, FechaEmision = hoy, ValidaHasta = hoy.AddDays(Cotizacion.DiasDeValidezPredeterminados),
            CreadoEn = Clock.GetUtcNow(),
        };
        if (oportunidadId is { } op && await ServicioOportunidades.ObtenerAsync(c, null, op) is { } oportunidad)
        {
            cotizacion.ClienteId ??= oportunidad.ClienteId;
            cotizacion.ContactoId = oportunidad.ContactoId;
        }
        cotizacion.Lineas.Add(new LineaCotizacion());
        return cotizacion;
    }

    /// <summary>Guarda la cotización (borrador o enviada). La primera vez recibe su número.</summary>
    public Task<string> GuardarAsync(Cotizacion cotizacion, CancellationToken ct = default)
    {
        cotizacion.Lineas.RemoveAll(l => l.EstaVacia);
        var errores = cotizacion.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var guardada = await ObtenerAsync(c, tx, cotizacion.Id);
            Require(guardada is null || guardada.Estado is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada,
                "La cotización ya fue aceptada o rechazada: crea una nueva.");
            cotizacion.Numero ??= guardada?.Numero ?? await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Cotizacion, Context.DeviceTag);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
            return cotizacion.Codigo;
        }, ct);
    }

    /// <summary>
    /// Cambia el estado y avanza la oportunidad vinculada (enviada → Cotizado, aceptada → Ganado). Al aceptarse publica
    /// <c>quote.accepted</c> con las líneas del catálogo, para que el ERP la convierta en pedido.
    /// </summary>
    public Task CambiarEstadoAsync(Guid id, EstadoCotizacion estado, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is not null, "Guarda la cotización antes de cambiar su estado.");
            Require(cotizacion!.Estado != estado, "La cotización ya está en ese estado.");
            Require(cotizacion.PedidoId is null, "La cotización ya tiene pedido: no puede cambiar de estado.");

            var oportunidad = cotizacion.OportunidadId is { } op ? await ServicioOportunidades.ObtenerAsync(c, tx, op) : null;
            var etapaAnterior = oportunidad?.Etapa;
            var cambioOportunidad = cotizacion.CambiarEstado(estado, oportunidad, now);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
            if (cambioOportunidad)
                await ServicioOportunidades.GuardarAsync(c, tx, now, oportunidad!, etapaAnterior);

            if (estado == EstadoCotizacion.Aceptada && cotizacion.ClienteId is { } cliente)
            {
                var moneda = await MonedaAsync(c, tx);
                await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.QuoteAccepted,
                    new QuoteAccepted(cotizacion.Id, cotizacion.Numero, cliente, LineasDelCatalogo(cotizacion), cotizacion.Totales.Total, moneda),
                    subjectType: "quote", subjectId: cotizacion.Numero, id: DeterministicId.From("quote_accepted", cotizacion.Id));
            }
            return 0;
        }, ct);

    /// <summary>Genera el pedido de una cotización aceptada: el WMS lo prepara y el ERP lo factura.</summary>
    public Task<string> GenerarPedidoAsync(Guid id, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is not null, "La cotización no existe.");
            Require(cotizacion!.Estado == EstadoCotizacion.Aceptada, "Solo se genera el pedido de una cotización aceptada.");
            Require(cotizacion.PedidoId is null, "La cotización ya tiene pedido.");
            Require(cotizacion.Lineas.All(l => l.ItemId is not null),
                "Para generar el pedido, todas las líneas deben ser artículos del catálogo (las de texto libre no se pueden despachar).");

            var pedidoId = PedidoIdPara(cotizacion.Id);
            var numero = "PED-" + cotizacion.Codigo.Replace("COT-", "", StringComparison.Ordinal);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderCreated,
                new OrderCreated(pedidoId, numero, cotizacion.ClienteId!.Value, cotizacion.SedeId, LineasDelCatalogo(cotizacion),
                    cotizacion.Totales.Total, await MonedaAsync(c, tx), cotizacion.Id),
                subjectType: "order", subjectId: numero, id: pedidoId);
            cotizacion.PedidoId = pedidoId;
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
            return numero;
        }, ct);

    public Task EliminarAsync(Guid id, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is null || cotizacion.Estado == EstadoCotizacion.Borrador, "Solo se elimina una cotización en borrador.");
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Cotizacion, id);
            return 0;
        }, ct);

    // ---------- Envío ----------

    /// <summary>Texto y enlace de WhatsApp (null si el teléfono no es un celular).</summary>
    public async Task<(string Mensaje, string? Enlace)> WhatsAppAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (cotizacion, cliente, contacto, empresa) = await DatosDeEnvioAsync(c, id);
        var saludo = contacto?.Nombres ?? (cliente.Kind == "Person" ? cliente.Name.Split(' ')[0] : null);
        var mensaje = MensajeWhatsApp.Cotizacion(cotizacion, empresa?.DisplayName ?? "nuestra empresa", saludo);
        return (mensaje, MensajeWhatsApp.Enlace(contacto?.Telefono ?? cliente.Phone, mensaje));
    }

    public async Task<byte[]> GenerarPdfAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (cotizacion, cliente, contacto, empresa) = await DatosDeEnvioAsync(c, id);
        var sede = cotizacion.SedeId is { } s
            ? await c.FirstOrDefaultAsync("SELECT address_line, city, phone FROM sites WHERE id = $id",
                r => new { Linea = r.StrOrNull("address_line"), Ciudad = r.StrOrNull("city"), Telefono = r.StrOrNull("phone") }, null, ("$id", s))
            : null;
        var direccion = string.Join(", ", new[] { sede?.Linea ?? empresa?.AddressLine, sede?.Ciudad ?? empresa?.City }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        var emisor = new EmisorPdf(empresa?.DisplayName ?? "Mi empresa", empresa?.LegalName, empresa?.TaxId,
            direccion.Length == 0 ? null : direccion, sede?.Telefono ?? empresa?.Phone, empresa?.Email);
        return GeneradorPdfCotizaciones.Generar(cotizacion, emisor, ClienteCotizacion.Crear(cliente, contacto));
    }

    // ---------- Internos ----------

    internal static async Task<Cotizacion?> ObtenerAsync(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        var cotizacion = await c.FirstOrDefaultAsync("SELECT * FROM cotizaciones WHERE id = $id", Registros.LeerCotizacion, tx, ("$id", id));
        if (cotizacion is not null)
            cotizacion.Lineas = await c.QueryAsync("SELECT * FROM cotizacion_lineas WHERE cotizacion_id = $id ORDER BY linea",
                Registros.LeerLinea, tx, ("$id", id));
        return cotizacion;
    }

    private async Task<(Cotizacion, PartyRow, Contacto?, CompanyInfo?)> DatosDeEnvioAsync(SqliteConnection c, Guid id)
    {
        var cotizacion = await ObtenerAsync(c, null, id);
        Require(cotizacion is not null, "Guarda la cotización primero.");
        var cliente = cotizacion!.ClienteId is { } cl
            ? await c.FirstOrDefaultAsync("SELECT * FROM parties WHERE id = $id", MasterDataService.ReadParty, null, ("$id", cl))
            : null;
        Require(cliente is not null, "La cotización no tiene cliente.");
        var contacto = cotizacion.ContactoId is { } k
            ? await c.FirstOrDefaultAsync("SELECT * FROM contactos WHERE id = $id", Registros.LeerContacto, null, ("$id", k))
            : null;
        var empresa = await c.FirstOrDefaultAsync("SELECT * FROM company WHERE id = 1", r => new CompanyInfo(r.Str("legal_name"),
            r.StrOrNull("trade_name"), r.StrOrNull("tax_id"), r.Str("country_code"), r.Str("currency_code"), r.StrOrNull("email"),
            r.StrOrNull("phone"), r.StrOrNull("address_line"), r.StrOrNull("city")));
        return (cotizacion, cliente!, contacto, empresa);
    }

    /// <summary>
    /// Líneas con artículo, con el precio como lo usa la suite (IGV incluido): si la cotización es "más IGV", el importe se
    /// lleva a precio con IGV.
    /// </summary>
    private static List<OrderLine> LineasDelCatalogo(Cotizacion cotizacion) => cotizacion.Lineas
        .Where(l => l.ItemId is not null && l.Cantidad > 0)
        .Select(l =>
        {
            var importe = cotizacion.ModoIgv == ModoIgv.Adicional
                ? Math.Round(l.Importe * (1 + Impuestos.TasaIgv), 2, MidpointRounding.AwayFromZero)
                : l.Importe;
            return new OrderLine(l.ItemId!.Value, l.Cantidad, Math.Round(importe / l.Cantidad, 4, MidpointRounding.AwayFromZero));
        })
        .ToList();

    private static async Task<string> MonedaAsync(SqliteConnection c, SqliteTransaction tx) =>
        await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1", tx) ?? "PEN";
}
