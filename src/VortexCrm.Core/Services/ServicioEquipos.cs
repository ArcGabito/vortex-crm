using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Equipos;
using VortexCrm.Core.Integration;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Equipos de venta, territorios y visibilidad por jerarquía (CRM-53) y comisiones (CRM-54). Equipos, visibilidad y plan
/// de comisiones son registros del CRM replicados (gana el más nuevo).
/// </summary>
public sealed class ServicioEquipos(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    public static readonly Guid VisibilidadId = Guid.Parse("5e7a0c3d-0000-4000-8000-00000000715b");
    public static readonly Guid ComisionesId = Guid.Parse("5e7a0c3d-0000-4000-8000-0000000c0315");

    // ---------- Equipos ----------

    public async Task<IReadOnlyList<EquipoVenta>> EquiposAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await EquiposAsync(c, null);
    }

    internal static async Task<List<EquipoVenta>> EquiposAsync(SqliteConnection c, SqliteTransaction? tx) =>
        (await c.QueryAsync("SELECT datos FROM equipos", r => JsonSerializer.Deserialize<EquipoVenta>(r.Str("datos"), VortexJson.Options)!, tx))
        .OrderBy(e => e.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task GuardarEquipoAsync(EquipoVenta equipo, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.AssignCustomers);
        equipo.Nombre = equipo.Nombre.Trim();
        equipo.Zonas = equipo.Zonas.Select(z => z.Trim()).Where(z => z.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        equipo.Miembros = equipo.Miembros.DistinctBy(m => m.Id).ToList();
        var errores = equipo.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var todos = await EquiposAsync(c, tx);
            // Un equipo no puede quedar dentro de sí mismo a través de otros.
            for (var padre = equipo.PadreId; padre is { } p; padre = todos.FirstOrDefault(e => e.Id == p)?.PadreId)
                Require(p != equipo.Id, T("error.equipo_padre"));
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Equipo, equipo.Id, equipo);
            await AuditAsync(c, tx, now, "team.saved", "team", equipo.Id, equipo.Nombre,
                $"{equipo.LiderNombre}; {equipo.Miembros.Count} vendedores; {string.Join(", ", equipo.Zonas)}");
            return 0;
        }, ct);
    }

    public Task EliminarEquipoAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.AssignCustomers);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var hijo = (await EquiposAsync(c, tx)).FirstOrDefault(e => e.PadreId == id);
            Require(hijo is null, T("error.equipo_con_hijos", hijo?.Nombre));
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Equipo, id);
            await AuditAsync(c, tx, now, "team.deleted", "team", id, null, null);
            return 0;
        }, ct);
    }

    // ---------- Visibilidad ----------

    public async Task<Visibilidad> VisibilidadAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await VisibilidadAsync(c, null);
    }

    private static async Task<Visibilidad> VisibilidadAsync(SqliteConnection c, SqliteTransaction? tx) =>
        await c.FirstOrDefaultAsync("SELECT datos FROM configuracion WHERE id = $id",
            r => JsonSerializer.Deserialize<VisibilidadDatos>(r.Str("datos"), VortexJson.Options)!, tx, ("$id", VisibilidadId)) is { } v
            ? v.Visibilidad
            : Visibilidad.Todo;

    public sealed record VisibilidadDatos(Guid Id, Visibilidad Visibilidad);

    public Task GuardarVisibilidadAsync(Visibilidad visibilidad, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Visibilidad, VisibilidadId, new VisibilidadDatos(VisibilidadId, visibilidad));
            await AuditAsync(c, tx, now, "settings.visibility", "settings", null, visibilidad.ToString(), null);
            return 0;
        }, ct);
    }

    /// <summary>
    /// De quiénes ve registros el usuario (JSON con sus Ids para filtrar en SQL), o null si ve todo: con visibilidad por
    /// jerarquía, un vendedor ve lo suyo y el líder de un equipo lo de sus vendedores y los de los equipos de abajo. El
    /// gerente (y quien edita lo de todos) ve todo. Lo que no tiene responsable lo ven todos.
    /// </summary>
    internal static async Task<string?> VisiblesAsync(SqliteConnection c, SqliteTransaction? tx, ICrmContext context, bool forzar = false)
    {
        if (context.Can(CrmPermissions.EditAny) || (!forzar && await VisibilidadAsync(c, tx) == Visibilidad.Todo))
            return null;
        return JsonSerializer.Serialize(Alcance(await EquiposAsync(c, tx), context.UserId).Select(id => id.ToString()));
    }

    /// <summary>El usuario y todos los que dependen de él por los equipos que lidera (y los de abajo).</summary>
    internal static HashSet<Guid> Alcance(IReadOnlyList<EquipoVenta> equipos, Guid? usuario)
    {
        var ids = new HashSet<Guid>();
        if (usuario is not { } yo)
            return ids;
        ids.Add(yo);
        var pendientes = new Queue<EquipoVenta>(equipos.Where(e => e.LiderId == yo));
        var vistos = new HashSet<Guid>();
        while (pendientes.TryDequeue(out var equipo))
        {
            if (!vistos.Add(equipo.Id))
                continue;
            foreach (var m in equipo.Miembros)
                ids.Add(m.Id);
            if (equipo.LiderId is { } lider)
                ids.Add(lider);
            foreach (var hijo in equipos.Where(e => e.PadreId == equipo.Id))
                pendientes.Enqueue(hijo);
        }
        return ids;
    }

    // ---------- Comisiones ----------

    public async Task<PlanComisiones> PlanAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await PlanAsync(c);
    }

    private static async Task<PlanComisiones> PlanAsync(SqliteConnection c) =>
        await c.FirstOrDefaultAsync("SELECT datos FROM configuracion WHERE id = $id",
            r => JsonSerializer.Deserialize<PlanDatos>(r.Str("datos"), VortexJson.Options)!.Plan, null, ("$id", ComisionesId))
        ?? PlanComisiones.Ninguno;

    public sealed record PlanDatos(Guid Id, PlanComisiones Plan);

    public Task GuardarPlanAsync(PlanComisiones plan, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        Require(plan.Base is BasesComision.Ganado or BasesComision.Cobrado, T("error.comision_base"));
        Require(plan.Tasa is >= 0 and <= 100 && plan.TasaSobreMeta is null or (>= 0 and <= 100)
                && (plan.TasaPorVendedor ?? new Dictionary<Guid, decimal>()).Values.All(t => t is >= 0 and <= 100), T("error.comision_tasa"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.PlanComisiones, ComisionesId, new PlanDatos(ComisionesId, plan));
            await AuditAsync(c, tx, now, "settings.commissions", "settings", null, plan.Base,
                $"{plan.Tasa:0.##} %{(plan.TasaSobreMeta is { } s ? $", sobre meta {s:0.##} %" : "")}");
            return 0;
        }, ct);
    }

    /// <summary>
    /// Comisiones del mes por vendedor: base (lo ganado o lo cobrado de su cartera), meta, avance, tasa y comisión. Cada uno
    /// ve la suya; el líder, las de su equipo; el gerente, todas.
    /// </summary>
    public async Task<IReadOnlyList<ComisionVendedor>> ComisionesAsync(DateOnly mes, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var plan = await PlanAsync(c);
        var inicio = new DateOnly(mes.Year, mes.Month, 1);
        var desde = SqliteExtensions.Time(new DateTimeOffset(inicio.ToDateTime(TimeOnly.MinValue), FormatoPeru.ZonaHoraria));
        var hasta = SqliteExtensions.Time(new DateTimeOffset(inicio.AddMonths(1).ToDateTime(TimeOnly.MinValue), FormatoPeru.ZonaHoraria));
        var moneda = (await FiscalAsync(c, null)).Moneda;
        var bases = plan.Base == BasesComision.Cobrado
            ? await c.QueryAsync("""
                SELECT p.owner_id AS id, MAX(p.owner_name) AS nombre, SUM(a.monto) AS base, COUNT(*) AS n
                FROM actividad a JOIN parties p ON p.id = a.cliente_id
                WHERE a.tipo = 'cobro' AND a.ocurrido_en >= $desde AND a.ocurrido_en < $hasta AND p.owner_id IS NOT NULL
                  AND COALESCE(a.moneda, $moneda) = $moneda
                GROUP BY p.owner_id
                """, Fila, null, ("$desde", desde), ("$hasta", hasta), ("$moneda", moneda))
            : await c.QueryAsync("""
                SELECT responsable_id AS id, MAX(responsable_nombre) AS nombre, SUM(monto) AS base, COUNT(*) AS n
                FROM oportunidades WHERE etapa = 'Ganado' AND cerrado_en >= $desde AND cerrado_en < $hasta AND responsable_id IS NOT NULL
                GROUP BY responsable_id
                """, Fila, null, ("$desde", desde), ("$hasta", hasta));
        var metas = (await c.QueryAsync("SELECT responsable_id, monto FROM metas WHERE mes = $mes AND responsable_id IS NOT NULL",
                r => (Id: r.Guid("responsable_id"), Monto: r.Dec("monto")), null, ("$mes", inicio.ToString("yyyy-MM-dd"))))
            .GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Monto));
        var visibles = await VisiblesAsync(c, null, Context, forzar: true) is { } json
            ? JsonSerializer.Deserialize<List<Guid>>(json)!.ToHashSet()
            : null;
        return bases.Where(b => visibles is null || visibles.Contains(b.Id))
            .Select(b =>
            {
                var meta = metas.TryGetValue(b.Id, out var m) ? m : (decimal?)null;
                return new ComisionVendedor(b.Id, b.Nombre ?? "?", b.Base, meta, plan.TasaDe(b.Id), plan.Calcular(b.Id, b.Base, meta), b.N);
            })
            .OrderByDescending(x => x.Comision).ThenBy(x => x.Nombre).ToList();

        static (Guid Id, string? Nombre, decimal Base, int N) Fila(SqliteDataReader r) =>
            (r.Guid("id"), r.StrOrNull("nombre"), r.Dec("base"), r.Int("n"));
    }
}
