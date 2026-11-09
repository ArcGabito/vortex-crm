using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Sync;

using static VortexCrm.Core.Texts.CoreText;

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
        var visibles = await ServicioEquipos.VisiblesAsync(c, null, Context);
        return await c.QueryAsync("""
            SELECT o.*, p.name AS cliente, TRIM(k.nombres || ' ' || k.apellidos) AS contacto
            FROM oportunidades o LEFT JOIN parties p ON p.id = o.cliente_id LEFT JOIN contactos k ON k.id = o.contacto_id
            WHERE ($cliente IS NULL OR o.cliente_id = $cliente)
              AND ($vis IS NULL OR o.responsable_id IS NULL OR o.responsable_id IN (SELECT value FROM json_each($vis)))
              AND ($q IS NULL OR o.titulo LIKE $like OR p.name LIKE $like)
            ORDER BY COALESCE(o.fecha_cierre_estimada, '9999') , o.creado_en DESC
            """, r => new OportunidadVista(Registros.LeerOportunidad(r), r.StrOrNull("cliente"), r.StrOrNull("contacto")), null,
            ("$cliente", clienteId), ("$q", Clean(busqueda)), ("$like", Like(busqueda)), ("$vis", visibles));
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
        RequirePermission(CrmPermissions.Sell);
        var errores = oportunidad.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var anterior = await ObtenerAsync(c, tx, oportunidad.Id);
            oportunidad.RecalcularMonto();
            if (anterior is not null)
                RequireOwner(anterior.ResponsableId, anterior.ResponsableNombre);
            oportunidad.ResponsableId ??= Context.UserId;
            oportunidad.ResponsableNombre ??= Context.UserName;
            if (anterior is null)
                oportunidad.EmbudoId ??= await ServicioEmbudos.EmbudoPorDefectoAsync(c, tx);
            var embudo = await ServicioEmbudos.EmbudoAsync(c, tx, oportunidad.EmbudoId);
            Require(oportunidad.EmbudoId is null || !embudo.EsPredeterminado, T("error.el_embudo_no_existe"));
            if (oportunidad.EtapaId is { } etapaId && oportunidad.Etapa.EstaAbierta())
            {
                var etapa = embudo.Etapa(etapaId);
                Require(etapa is not null, T("error.la_etapa_no_es_del_embudo"));
                oportunidad.CambiarEtapa(etapa!.Equivale, now);
            }
            await GuardarAsync(c, tx, now, oportunidad, anterior?.Etapa);
            if (anterior is not null && anterior.Etapa != oportunidad.Etapa)
                await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.StageChanged, "deal", oportunidad.Id, oportunidad.Titulo,
                    $"{anterior.Etapa.Nombre()} → {oportunidad.Etapa.Nombre()}");
            return 0;
        }, ct);
    }

    public Task CambiarEtapaAsync(Guid id, EtapaOportunidad etapa, string? motivoPerdida = null, CancellationToken ct = default) =>
        CambiarEtapaAsync(id, etapa, null, motivoPerdida, null, ct);

    /// <summary>Mueve la oportunidad a una etapa abierta de su embudo.</summary>
    public Task MoverAEtapaAsync(Guid id, Guid etapaId, CancellationToken ct = default) =>
        CambiarEtapaAsync(id, null, etapaId, null, null, ct);

    /// <summary>Marca la oportunidad como perdida con el motivo (de la lista de ajustes u otro) y, si se sabe, el competidor.</summary>
    public Task PerderAsync(Guid id, string? motivo, string? competidor, CancellationToken ct = default) =>
        CambiarEtapaAsync(id, EtapaOportunidad.Perdido, null, motivo, competidor, ct);

    private Task CambiarEtapaAsync(Guid id, EtapaOportunidad? etapaComun, Guid? etapaId, string? motivoPerdida, string? competidor,
        CancellationToken ct)
    {
        RequirePermission(CrmPermissions.Sell);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var oportunidad = await ObtenerAsync(c, tx, id);
            Require(oportunidad is not null, T("error.la_oportunidad_no_existe"));
            RequireOwner(oportunidad!.ResponsableId, oportunidad.ResponsableNombre);
            var anterior = oportunidad.Etapa;
            var embudo = await ServicioEmbudos.EmbudoAsync(c, tx, oportunidad.EmbudoId);
            var etapa = etapaComun ?? EtapaOportunidad.Prospecto;
            if (etapaId is { } eid)
            {
                var destino = embudo.Etapa(eid);
                Require(destino is not null, T("error.la_etapa_no_es_del_embudo"));
                etapa = destino!.Equivale;
            }
            oportunidad.CambiarEtapa(etapa, now);
            oportunidad.EtapaId = etapaId ?? oportunidad.EtapaId;
            if (etapa == EtapaOportunidad.Perdido)
            {
                oportunidad.MotivoPerdida = Clean(motivoPerdida);
                oportunidad.Competidor = Clean(competidor) ?? oportunidad.Competidor;
            }
            await GuardarAsync(c, tx, now, oportunidad, anterior);
            if (anterior != etapa)
                await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.StageChanged, "deal", id, oportunidad.Titulo,
                    $"{anterior.Nombre()} → {etapa.Nombre()}{(oportunidad.MotivoPerdida is { } m && etapa == EtapaOportunidad.Perdido ? $": {m}" : "")}");
            return 0;
        }, ct);
    }

    public Task EliminarAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Delete);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var oportunidad = await ObtenerAsync(c, tx, id);
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Oportunidad, id);
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Deleted, "deal", id, oportunidad?.Titulo, null);
            return 0;
        }, ct);
    }

    // ---------- Internos (los usa también el servicio de cotizaciones) ----------

    /// <summary>La oportunidad con sus productos.</summary>
    internal static async Task<Oportunidad?> ObtenerAsync(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        var oportunidad = await c.FirstOrDefaultAsync("SELECT * FROM oportunidades WHERE id = $id", Registros.LeerOportunidad, tx, ("$id", id));
        if (oportunidad is not null)
            oportunidad.Lineas = await c.QueryAsync("SELECT * FROM oportunidad_lineas WHERE oportunidad_id = $id ORDER BY linea",
                Registros.LeerLineaOportunidad, tx, ("$id", id));
        return oportunidad;
    }

    /// <summary>
    /// Guarda y replica la oportunidad. Si acaba de ganarse, publica <c>deal.won</c> con un Id determinista: si se reabre y
    /// se vuelve a ganar (o dos equipos la ganan a la vez), el ERP la cuenta una sola vez.
    /// </summary>
    internal static async Task GuardarAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Oportunidad oportunidad,
        EtapaOportunidad? etapaAnterior)
    {
        // La etapa del embudo sigue a la etapa común: si una cotización la hizo avanzar (o se cerró o reabrió), se ubica en
        // la primera etapa del embudo que equivale.
        if (!oportunidad.Etapa.EstaAbierta())
            oportunidad.EtapaId = null;
        else
        {
            var embudo = await ServicioEmbudos.EmbudoAsync(c, tx, oportunidad.EmbudoId);
            if (embudo.Etapa(oportunidad.EtapaId)?.Equivale != oportunidad.Etapa)
                oportunidad.EtapaId = embudo.PrimeraQueEquivale(oportunidad.Etapa)?.Id
                    ?? embudo.Etapas.LastOrDefault(e => e.Equivale < oportunidad.Etapa)?.Id ?? embudo.Etapas.FirstOrDefault()?.Id;
        }
        await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Oportunidad, oportunidad.Id, Registros.Datos(oportunidad));
        if (oportunidad.Etapa != EtapaOportunidad.Ganado || etapaAnterior == EtapaOportunidad.Ganado || oportunidad.ClienteId is not { } cliente)
            return;

        var cotizacion = await c.ScalarAsync<string?>("""
            SELECT id FROM cotizaciones WHERE oportunidad_id = $id AND estado = 'Aceptada' ORDER BY actualizado_en DESC LIMIT 1
            """, tx, ("$id", oportunidad.Id));
        var moneda = await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1", tx) ?? "PEN";
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.DealWon,
            new DealWon(oportunidad.Id, cliente, oportunidad.Monto, moneda, cotizacion is null ? null : Guid.Parse(cotizacion), oportunidad.EmbudoId,
                oportunidad.Lineas is { Count: > 0 } lineas ? lineas.Select(l => new DealLine(l.ItemId, l.Descripcion, l.Cantidad, l.PrecioUnitario)).ToList() : null),
            subjectType: "deal", subjectId: oportunidad.Titulo, id: DeterministicId.From("deal_won", oportunidad.Id));
    }
}
