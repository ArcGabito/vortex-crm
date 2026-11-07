using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Embudos de ventas configurables (etapas, probabilidad, colores) y metas de ventas por mes, con el pronóstico. Los
/// configura el gerente de ventas; se replican a los demás equipos como registros del CRM.
/// </summary>
public sealed class ServicioEmbudos(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    // ---------- Embudos ----------

    /// <summary>Embudos en uso (sin los archivados). Si no hay ninguno configurado, el predeterminado.</summary>
    public async Task<IReadOnlyList<Embudo>> ListarAsync(bool incluirArchivados = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var lista = await EmbudosAsync(c, null, incluirArchivados);
        return lista.Count > 0 ? lista : [Embudo.Predeterminado];
    }

    public async Task<Embudo> ObtenerAsync(Guid? id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await EmbudoAsync(c, null, id);
    }

    public Task GuardarAsync(Embudo embudo, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        Require(!embudo.EsPredeterminado, T("error.el_embudo_necesita_nombre"));
        var errores = embudo.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var todos = await EmbudosAsync(c, tx, incluirArchivados: true);
            // El primero que se crea queda por defecto; marcar uno por defecto se lo quita a los demás.
            var nuevo = embudo with { PorDefecto = embudo.PorDefecto || todos.All(e => e.Id == embudo.Id || e.Archivado) };
            if (nuevo.PorDefecto)
            {
                foreach (var otro in todos.Where(e => e.PorDefecto && e.Id != nuevo.Id))
                    await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Embudo, otro.Id, Registros.Datos(otro with { PorDefecto = false }));
            }
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Embudo, nuevo.Id, Registros.Datos(nuevo));
            await AuditAsync(c, tx, now, AuditActions.SettingsChanged, "pipeline", nuevo.Id, nuevo.Nombre,
                string.Join(" → ", nuevo.Etapas.Select(e => $"{e.Nombre} ({e.Probabilidad} %)")));
            return 0;
        }, ct);
    }

    /// <summary>Archiva un embudo: ya no se ofrece para oportunidades nuevas; las que tiene siguen en él.</summary>
    public Task ArchivarAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var embudo = await EmbudoAsync(c, tx, id);
            Require(!embudo.EsPredeterminado, T("error.el_embudo_no_existe"));
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Embudo, id, Registros.Datos(embudo with { Archivado = true, PorDefecto = false }));
            await AuditAsync(c, tx, now, AuditActions.SettingsChanged, "pipeline", id, embudo.Nombre, "Archivado");
            return 0;
        }, ct);
    }

    internal static Task<List<Embudo>> EmbudosAsync(SqliteConnection c, SqliteTransaction? tx, bool incluirArchivados) =>
        c.QueryAsync($"SELECT * FROM embudos{(incluirArchivados ? "" : " WHERE archivado = 0")} ORDER BY por_defecto DESC, nombre",
            Registros.LeerEmbudo, tx);

    /// <summary>El embudo pedido, o (sin Id o si no existe) el predeterminado.</summary>
    internal static async Task<Embudo> EmbudoAsync(SqliteConnection c, SqliteTransaction? tx, Guid? id) =>
        id is { } e && await c.FirstOrDefaultAsync("SELECT * FROM embudos WHERE id = $id", Registros.LeerEmbudo, tx, ("$id", e)) is { } embudo
            ? embudo
            : Embudo.Predeterminado;

    /// <summary>Embudo de las oportunidades nuevas: el marcado por defecto (null = el predeterminado).</summary>
    internal static async Task<Guid?> EmbudoPorDefectoAsync(SqliteConnection c, SqliteTransaction? tx) =>
        await c.ScalarAsync<string?>("SELECT id FROM embudos WHERE por_defecto = 1 AND archivado = 0 LIMIT 1", tx) is { } id
            ? Guid.Parse(id)
            : null;

    // ---------- Metas y pronóstico ----------

    public async Task<IReadOnlyList<Meta>> MetasAsync(DateOnly mes, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM metas WHERE mes = $mes ORDER BY responsable_nombre", Registros.LeerMeta, null,
            ("$mes", Registros.Fecha(Pronostico.Mes(mes))));
    }

    /// <summary>Fija la meta del mes de un vendedor (o del equipo, sin vendedor). Si ya había una, la reemplaza.</summary>
    public Task GuardarMetaAsync(DateOnly mes, Guid? responsableId, string? responsableNombre, decimal monto, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        Require(monto > 0, T("error.el_monto_debe_ser_mayor"));
        var primero = Pronostico.Mes(mes);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var existente = await c.ScalarAsync<string?>("""
                SELECT id FROM metas WHERE mes = $mes AND (responsable_id = $responsable OR ($responsable IS NULL AND responsable_id IS NULL))
                """, tx, ("$mes", Registros.Fecha(primero)), ("$responsable", responsableId));
            var meta = new Meta(existente is null ? Guid.NewGuid() : Guid.Parse(existente), primero, responsableId,
                responsableId is null ? null : Clean(responsableNombre), monto);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Meta, meta.Id, Registros.Datos(meta));
            await AuditAsync(c, tx, now, AuditActions.SettingsChanged, "goal", meta.Id, $"{primero:yyyy-MM}",
                $"{meta.ResponsableNombre ?? "Equipo"}: {monto:0.##}");
            return 0;
        }, ct);
    }

    public Task EliminarMetaAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Meta, id);
            await AuditAsync(c, tx, now, AuditActions.Deleted, "goal", id, null, null);
            return 0;
        }, ct);
    }

    /// <summary>Pronóstico ponderado de los próximos meses y avance de cada vendedor en el mes actual.</summary>
    public async Task<Pronostico> PronosticoAsync(int meses = 3, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var oportunidades = await c.QueryAsync("SELECT * FROM oportunidades", Registros.LeerOportunidad);
        var embudos = await EmbudosAsync(c, null, incluirArchivados: true);
        var metas = await c.QueryAsync("SELECT * FROM metas", Registros.LeerMeta);
        return Pronostico.Calcular(oportunidades, embudos, metas, FormatoPeru.Hoy(Clock), meses);
    }
}
