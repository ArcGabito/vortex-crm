using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Sync;

namespace VortexCrm.Core.Services;

/// <summary>
/// El pipeline de ventas. Al ganar una oportunidad se publica <c>deal.won</c> (lo registra el ERP); todo cambio se
/// replica a los demás equipos del CRM.
/// </summary>
public sealed class ServicioOportunidades(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    public async Task<IReadOnlyList<OportunidadVista>> ListarAsync(string? busqueda = null, Guid? clienteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT o.*, p.name AS cliente, TRIM(k.nombres || ' ' || k.apellidos) AS contacto
            FROM oportunidades o LEFT JOIN parties p ON p.id = o.cliente_id LEFT JOIN contactos k ON k.id = o.contacto_id
            WHERE ($cliente IS NULL OR o.cliente_id = $cliente)
              AND ($q IS NULL OR o.titulo LIKE $like OR p.name LIKE $like)
            ORDER BY COALESCE(o.fecha_cierre_estimada, '9999') , o.creado_en DESC
            """, r => new OportunidadVista(Registros.LeerOportunidad(r), r.StrOrNull("cliente"), r.StrOrNull("contacto")), null,
            ("$cliente", clienteId), ("$q", Clean(busqueda)), ("$like", Like(busqueda)));
    }

    public async Task<Oportunidad?> ObtenerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ObtenerAsync(c, null, id);
    }

    public async Task<IReadOnlyList<Option>> OpcionesAsync(Guid? clienteId, CancellationToken ct = default) =>
        (await ListarAsync(null, clienteId, ct)).Where(v => v.Oportunidad.Etapa.EstaAbierta() || v.Oportunidad.ClienteId == clienteId)
            .Select(v => new Option(v.Oportunidad.Id, v.Oportunidad.Titulo, v.Cliente)).ToList();

    public Task GuardarAsync(Oportunidad oportunidad, CancellationToken ct = default)
    {
        var errores = oportunidad.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var anterior = await ObtenerAsync(c, tx, oportunidad.Id);
            await GuardarAsync(c, tx, now, oportunidad, anterior?.Etapa);
            return 0;
        }, ct);
    }

    public Task CambiarEtapaAsync(Guid id, EtapaOportunidad etapa, string? motivoPerdida = null, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var oportunidad = await ObtenerAsync(c, tx, id);
            Require(oportunidad is not null, "La oportunidad no existe.");
            var anterior = oportunidad!.Etapa;
            oportunidad.CambiarEtapa(etapa, now);
            if (etapa == EtapaOportunidad.Perdido)
                oportunidad.MotivoPerdida = Clean(motivoPerdida);
            await GuardarAsync(c, tx, now, oportunidad, anterior);
            return 0;
        }, ct);

    public Task EliminarAsync(Guid id, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Oportunidad, id);
            return 0;
        }, ct);

    // ---------- Internos (los usa también el servicio de cotizaciones) ----------

    internal static Task<Oportunidad?> ObtenerAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync("SELECT * FROM oportunidades WHERE id = $id", Registros.LeerOportunidad, tx, ("$id", id));

    /// <summary>
    /// Guarda y replica la oportunidad. Si acaba de ganarse, publica <c>deal.won</c> con un Id determinista: si se reabre y
    /// se vuelve a ganar (o dos equipos la ganan a la vez), el ERP la cuenta una sola vez.
    /// </summary>
    internal static async Task GuardarAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Oportunidad oportunidad,
        EtapaOportunidad? etapaAnterior)
    {
        await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Oportunidad, oportunidad.Id, Registros.Datos(oportunidad));
        if (oportunidad.Etapa != EtapaOportunidad.Ganado || etapaAnterior == EtapaOportunidad.Ganado || oportunidad.ClienteId is not { } cliente)
            return;

        var cotizacion = await c.ScalarAsync<string?>("""
            SELECT id FROM cotizaciones WHERE oportunidad_id = $id AND estado = 'Aceptada' ORDER BY actualizado_en DESC LIMIT 1
            """, tx, ("$id", oportunidad.Id));
        var moneda = await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1", tx) ?? "PEN";
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.DealWon,
            new DealWon(oportunidad.Id, cliente, oportunidad.Monto, moneda, cotizacion is null ? null : Guid.Parse(cotizacion)),
            subjectType: "deal", subjectId: oportunidad.Titulo, id: DeterministicId.From("deal_won", oportunidad.Id));
    }
}
