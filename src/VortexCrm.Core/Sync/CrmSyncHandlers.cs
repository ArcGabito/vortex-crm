using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Services;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Sync;

/// <summary>
/// Qué hace el CRM con lo que llega del hub:
/// <list type="bullet">
/// <item>Actualiza la copia de datos maestros (artículos, sedes, clientes, empresa).</item>
/// <item>Replica los contactos, oportunidades, cotizaciones, tareas y ajustes de <b>otros equipos del CRM</b>
/// (<c>crm.record.*</c>, el último cambio gana). Requiere <see cref="VortexSyncOptions.IncludeOwnEvents"/>.</item>
/// <item>Arma la actividad del cliente con lo que informan los demás sistemas: ventas del POS, comprobantes y cobros del
/// ERP, despachos del WMS.</item>
/// </list>
/// Cada evento se aplica una sola vez (tabla <c>processed_events</c>) y en una transacción junto con su efecto.
/// </summary>
public sealed class CrmSyncHandlers(CrmDatabase db, MasterDataService master, TimeProvider clock)
{
    public const string ProductCode = "CRM";

    /// <summary>Opciones de <see cref="VortexSync"/> que necesita el CRM (recibir lo que hacen sus otros equipos).</summary>
    public static VortexSyncOptions SyncOptions => new() { IncludeOwnEvents = true };

    /// <summary>Se avisa cuando llegan tareas de otro equipo (para reprogramar los avisos).</summary>
    public event Action? TasksChanged;

    /// <summary>Registra todos los handlers. Llamar antes de la primera sincronización.</summary>
    public VortexSync Register(VortexSync sync) => sync
        .OnItems((c, ct) => master.ApplyItemAsync(c, ct))
        .OnSites((c, ct) => master.ApplySiteAsync(c, ct))
        .OnParties((c, ct) => master.ApplyPartyAsync(c, ct))
        .OnCompany((c, ct) => master.ApplyCompanyAsync(c, ct))
        .OnEvent(SuiteEvents.RecordUpserted, HandleRecordUpsertedAsync)
        .OnEvent(SuiteEvents.RecordDeleted, HandleRecordDeletedAsync)
        .OnEvent(SuiteEvents.SaleCompleted, HandleSaleCompletedAsync)
        .OnEvent(SuiteEvents.InvoiceIssued, HandleInvoiceIssuedAsync)
        .OnEvent(SuiteEvents.PaymentReceived, HandlePaymentReceivedAsync)
        .OnEvent(SuiteEvents.ShipmentDispatched, HandleShipmentDispatchedAsync);

    // ---------- Otros equipos del CRM ----------

    public async Task HandleRecordUpsertedAsync(VortexEvent e, CancellationToken ct = default)
    {
        var tarea = false;
        await ApplyOnceAsync<CrmRecord>(e, async (r, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: crm.record solo lo publica el CRM";
            if (r.Data is not { ValueKind: JsonValueKind.Object } data)
                return "sin data";
            var aplicado = await Registros.AplicarAsync(c, tx, r.Kind, data, r.UpdatedAt, local: false);
            tarea = aplicado && r.Kind == CrmRecordKinds.Tarea;
            return aplicado ? null : "sin cambios (ya tenía esta versión o una más nueva)";
        }, ct);
        if (tarea)
            TasksChanged?.Invoke();
    }

    public Task HandleRecordDeletedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<CrmRecord>(e, async (r, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: crm.record solo lo publica el CRM";
            return await Registros.BorrarAsync(c, tx, r.Kind, r.Id, r.UpdatedAt) ? null : "nada que borrar";
        }, ct);

    // ---------- Actividad del cliente ----------

    public Task HandleSaleCompletedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleCompleted>(e, async (s, c, tx) =>
        {
            if (s.CustomerId is not { } cliente)
                return "venta sin cliente";
            await ServicioClientes.RegistrarActividadAsync(c, tx, e.Id, cliente, TiposActividad.Venta, T("actividad.compra_en_caja", s.Number).Trim(),
                null, s.Total, s.CurrencyCode, e.Source, e.OccurredAt);
            return null;
        }, ct);

    public Task HandleInvoiceIssuedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<InvoiceIssued>(e, async (i, c, tx) =>
        {
            if (i.CustomerId is not { } cliente)
                return "comprobante sin cliente";
            var tipo = i.Kind == "boleta" ? T("actividad.boleta") : T("actividad.factura");
            await ServicioClientes.RegistrarActividadAsync(c, tx, e.Id, cliente, TiposActividad.Comprobante, $"{tipo} {i.Number}".Trim(),
                null, i.Total, i.CurrencyCode, e.Source, e.OccurredAt);
            return null;
        }, ct);

    public Task HandlePaymentReceivedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<PaymentReceived>(e, async (p, c, tx) =>
        {
            if (p.CustomerId is not { } cliente)
                return "cobro sin cliente";
            await ServicioClientes.RegistrarActividadAsync(c, tx, e.Id, cliente, TiposActividad.Cobro, T("actividad.pago_recibido", p.Number).Trim(),
                e.SubjectId is null ? null : $"Comprobante {e.SubjectId}", p.Amount, p.CurrencyCode, e.Source, e.OccurredAt);
            return null;
        }, ct);

    public Task HandleShipmentDispatchedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<ShipmentDispatched>(e, async (d, c, tx) =>
        {
            if (d.CustomerId is not { } cliente || cliente == Guid.Empty)
                return "despacho sin cliente";
            var detalle = string.Join(" · ", new[] { d.Carrier, d.TrackingNumber is null ? null : T("actividad.guia", d.TrackingNumber) }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            await ServicioClientes.RegistrarActividadAsync(c, tx, e.Id, cliente, TiposActividad.Despacho, T("actividad.pedido_despachado", d.Number).Trim(),
                detalle.Length == 0 ? null : detalle, null, null, e.Source, e.OccurredAt);
            return null;
        }, ct);

    // ---------- Internos ----------

    /// <summary>
    /// Aplica un evento una sola vez. Un esquema futuro o un <c>data</c> ilegible se registran como ignorados (nunca
    /// se van a poder aplicar y no deben trabar el feed); un error de la base sí se propaga para reintentar.
    /// </summary>
    private async Task ApplyOnceAsync<T>(VortexEvent e, Func<T, SqliteConnection, SqliteTransaction, Task<string?>> apply,
        CancellationToken ct) where T : class
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE event_id = $id", tx, ("$id", e.Id)) > 0)
            return;

        string? note;
        if (e.SchemaVersion != 1)
        {
            note = $"ignorado: schemaVersion {e.SchemaVersion} no soportada";
        }
        else
        {
            T? data = null;
            try
            {
                data = e.DataAs<T>();
                note = data is null ? "ignorado: data vacía" : null;
            }
            catch (JsonException ex)
            {
                note = $"ignorado: data inválida ({ex.Message})";
            }
            if (data is not null)
                note = await apply(data, c, tx);
        }

        await c.ExecAsync("INSERT INTO processed_events (event_id, type, processed_at, note) VALUES ($id, $type, $now, $note)", tx,
            ("$id", e.Id), ("$type", e.Type), ("$now", clock.GetUtcNow()), ("$note", note));
        await tx.CommitAsync(ct);
    }
}
