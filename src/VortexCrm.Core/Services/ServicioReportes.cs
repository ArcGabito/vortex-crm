using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Ventas;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>Etapa abierta del embudo: oportunidades que hoy están en ella.</summary>
public sealed record EtapaEmbudo(EtapaOportunidad Etapa, int Oportunidades, decimal Monto);

/// <summary>Resultado de un vendedor en el periodo (ranking, actividad y conversión).</summary>
public sealed record FilaVendedor(Guid? Id, string Nombre, int Ganadas, decimal MontoGanado, int Perdidas, decimal? TasaCierre,
    decimal? CicloDias, int Interacciones, int TareasHechas, int LeadsConvertidos, decimal? Meta);

/// <summary>
/// Reporte comercial de un periodo (CRM-62): embudo de lo abierto, conversión de leads, cotizaciones y oportunidades,
/// ciclo de venta, actividad y ranking por vendedor. Respeta la visibilidad por jerarquía: el vendedor ve lo suyo, el líder
/// su equipo y el gerente todo.
/// </summary>
/// <param name="Alcance">Quién lo ve: <c>todo</c> (gerente), <c>equipo</c> (líder) o <c>propio</c> (vendedor).</param>
public sealed record ReporteVentas(DateOnly Desde, DateOnly Hasta, string Alcance, string Moneda, IReadOnlyList<EtapaEmbudo> Embudo,
    int LeadsNuevos, int LeadsConvertidos, int CotizacionesEnviadas, int CotizacionesAceptadas, int Ganadas, decimal MontoGanado, int Perdidas,
    decimal? TicketPromedio, decimal? CicloDias, int Interacciones, int TareasHechas, IReadOnlyList<FilaVendedor> Vendedores,
    IReadOnlyList<(string Motivo, int Veces)> MotivosPerdida)
{
    public decimal? ConversionLeads => LeadsNuevos > 0 ? Math.Round(100m * LeadsConvertidos / LeadsNuevos, 1) : null;
    public decimal? AceptacionCotizaciones => CotizacionesEnviadas > 0 ? Math.Round(100m * CotizacionesAceptadas / CotizacionesEnviadas, 1) : null;
    public decimal? TasaCierre => Ganadas + Perdidas > 0 ? Math.Round(100m * Ganadas / (Ganadas + Perdidas), 1) : null;
    public decimal MontoAbierto => Embudo.Sum(e => e.Monto);
}

/// <summary>Reportes y tableros por rol (fase 5d), con exportación a CSV (Excel) y PDF.</summary>
public sealed class ServicioReportes(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    /// <param name="hasta">Último día incluido.</param>
    public async Task<ReporteVentas> GenerarAsync(DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        Require(hasta >= desde, T("error.periodo_invalido"));
        Require(hasta.DayNumber - desde.DayNumber <= 366, T("error.periodo_muy_largo"));
        await using var c = await Db.OpenAsync(ct);
        var visibles = await ServicioEquipos.VisiblesAsync(c, null, Context, forzar: true) is { } json
            ? JsonSerializer.Deserialize<List<Guid>>(json)!.ToHashSet()
            : null;
        bool Ve(Guid? id) => visibles is null || id is null || visibles.Contains(id.Value);
        var alcance = visibles is null ? "todo" : visibles.Count > 1 ? "equipo" : "propio";

        var ini = Instante(desde);
        var fin = Instante(hasta.AddDays(1));
        var moneda = (await FiscalAsync(c, null)).Moneda;

        var ops = (await c.QueryAsync("""
            SELECT id, monto, etapa, responsable_id, responsable_nombre, creado_en, cerrado_en, motivo_perdida FROM oportunidades
            """, r => new
        {
            Monto = r.Dec("monto"), Etapa = Enum.TryParse<EtapaOportunidad>(r.Str("etapa"), out var e) ? e : EtapaOportunidad.Prospecto,
            Responsable = r.GuidOrNull("responsable_id"), Nombre = r.StrOrNull("responsable_nombre"), Creado = r.Time("creado_en"),
            Cerrado = r.TimeOrNull("cerrado_en"), Motivo = r.StrOrNull("motivo_perdida"),
        })).Where(o => Ve(o.Responsable)).ToList();
        var (iniT, finT) = (Dia(desde), Dia(hasta.AddDays(1)));
        var cerradas = ops.Where(o => o.Cerrado is { } x && x >= iniT && x < finT).ToList();
        var ganadas = cerradas.Where(o => o.Etapa == EtapaOportunidad.Ganado).ToList();
        var perdidas = cerradas.Where(o => o.Etapa == EtapaOportunidad.Perdido).ToList();

        var leads = (await c.QueryAsync("""
            SELECT responsable_id, responsable_nombre, estado, creado_en, convertido_en FROM leads WHERE creado_en >= $ini AND creado_en < $fin
            """, r => new { Responsable = r.GuidOrNull("responsable_id"), Nombre = r.StrOrNull("responsable_nombre"), Estado = r.Str("estado") },
            null, ("$ini", ini), ("$fin", fin))).Where(l => Ve(l.Responsable)).ToList();

        var cotizaciones = (await c.QueryAsync("""
            SELECT responsable_id, estado FROM cotizaciones WHERE fecha_emision >= $d AND fecha_emision <= $h
            """, r => new { Responsable = r.GuidOrNull("responsable_id"), Estado = r.Str("estado") }, null,
            ("$d", desde.ToString("yyyy-MM-dd")), ("$h", hasta.ToString("yyyy-MM-dd")))).Where(q => Ve(q.Responsable)).ToList();
        var enviadas = cotizaciones.Count(q => q.Estado is not (nameof(EstadoCotizacion.Borrador) or nameof(EstadoCotizacion.Reemplazada)));
        var aceptadas = cotizaciones.Count(q => q.Estado == nameof(EstadoCotizacion.Aceptada));

        var interacciones = (await c.QueryAsync("""
            SELECT responsable_id, responsable_nombre FROM interacciones WHERE fecha >= $ini AND fecha < $fin
            """, r => (Id: r.GuidOrNull("responsable_id"), Nombre: r.StrOrNull("responsable_nombre")), null, ("$ini", ini), ("$fin", fin)))
            .Where(i => Ve(i.Id)).ToList();
        var tareas = (await c.QueryAsync("""
            SELECT responsable_id, responsable_nombre FROM tareas WHERE completada_en >= $ini AND completada_en < $fin
            """, r => (Id: r.GuidOrNull("responsable_id"), Nombre: r.StrOrNull("responsable_nombre")), null, ("$ini", ini), ("$fin", fin)))
            .Where(t => Ve(t.Id)).ToList();

        var metas = (await c.QueryAsync("SELECT responsable_id, mes, monto FROM metas WHERE responsable_id IS NOT NULL",
                r => (Id: r.Guid("responsable_id"), Mes: DateOnly.Parse(r.Str("mes"), CultureInfo.InvariantCulture), Monto: r.Dec("monto"))))
            .Where(m => m.Mes >= new DateOnly(desde.Year, desde.Month, 1) && m.Mes <= hasta)
            .GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Monto));

        static decimal? Ciclo<T>(IReadOnlyCollection<T> won, Func<T, double> dias) => won.Count > 0 ? Math.Round((decimal)won.Average(dias), 1) : null;

        var nombres = new Dictionary<Guid, string>();
        void Nombre(Guid? id, string? nombre)
        {
            if (id is { } k && !string.IsNullOrWhiteSpace(nombre))
                nombres[k] = nombre;
        }
        foreach (var o in ops) Nombre(o.Responsable, o.Nombre);
        foreach (var l in leads) Nombre(l.Responsable, l.Nombre);
        foreach (var i in interacciones) Nombre(i.Id, i.Nombre);
        foreach (var t in tareas) Nombre(t.Id, t.Nombre);

        var personas = cerradas.Select(o => o.Responsable).Concat(interacciones.Select(i => i.Id)).Concat(tareas.Select(t => t.Id))
            .Concat(leads.Where(l => l.Estado == EstadosLead.Convertido).Select(l => l.Responsable)).Distinct().ToList();
        var filas = personas.Select(p =>
        {
            var g = ganadas.Where(o => o.Responsable == p).ToList();
            var pe = perdidas.Count(o => o.Responsable == p);
            return new FilaVendedor(p, p is { } k ? nombres.GetValueOrDefault(k, "?") : T("reportes.sin_responsable"), g.Count, g.Sum(o => o.Monto), pe,
                g.Count + pe > 0 ? Math.Round(100m * g.Count / (g.Count + pe), 1) : null,
                Ciclo(g, o => (o.Cerrado!.Value - o.Creado).TotalDays),
                interacciones.Count(i => i.Id == p), tareas.Count(t => t.Id == p),
                leads.Count(l => l.Responsable == p && l.Estado == EstadosLead.Convertido),
                p is { } m && metas.TryGetValue(m, out var meta) ? meta : null);
        }).OrderByDescending(f => f.MontoGanado).ThenByDescending(f => f.Ganadas).ThenBy(f => f.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();

        var embudo = new[] { EtapaOportunidad.Prospecto, EtapaOportunidad.Cotizado, EtapaOportunidad.Negociacion }
            .Select(e => new EtapaEmbudo(e, ops.Count(o => o.Etapa == e), ops.Where(o => o.Etapa == e).Sum(o => o.Monto))).ToList();

        return new ReporteVentas(desde, hasta, alcance, moneda, embudo, leads.Count, leads.Count(l => l.Estado == EstadosLead.Convertido),
            enviadas, aceptadas, ganadas.Count, ganadas.Sum(o => o.Monto), perdidas.Count,
            ganadas.Count > 0 ? Math.Round(ganadas.Average(o => o.Monto), 2) : null, Ciclo(ganadas, o => (o.Cerrado!.Value - o.Creado).TotalDays),
            interacciones.Count, tareas.Count, filas,
            perdidas.GroupBy(o => string.IsNullOrWhiteSpace(o.Motivo) ? T("reportes.sin_motivo") : o.Motivo!.Trim())
                .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList());
    }

    private static DateTimeOffset Dia(DateOnly dia) => new(dia.ToDateTime(TimeOnly.MinValue), FormatoPeru.ZonaHoraria);

    private static string Instante(DateOnly dia) => SqliteExtensions.Time(Dia(dia));

    /// <summary>El reporte en CSV (UTF-8, separado por comas) para abrirlo en Excel: resumen y ranking por vendedor.</summary>
    public static string Csv(ReporteVentas r)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(decimal? v) => v is { } x ? x.ToString("0.##", inv) : "";
        string Celda(string? v)
        {
            var s = v ?? "";
            if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@')
                s = "'" + s;
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Celda(T("reportes.periodo")), $"{r.Desde:yyyy-MM-dd}", $"{r.Hasta:yyyy-MM-dd}"));
        foreach (var (k, v) in Resumen(r))
            sb.AppendLine(string.Join(',', Celda(k), v));
        sb.AppendLine();
        sb.AppendLine(string.Join(',', new[] { "reportes.vendedor", "reportes.ganadas", "reportes.monto_ganado", "reportes.perdidas",
            "reportes.tasa_cierre", "reportes.ciclo_dias", "reportes.interacciones", "reportes.tareas", "reportes.leads_convertidos", "reportes.meta" }
            .Select(k => Celda(T(k)))));
        foreach (var f in r.Vendedores)
            sb.AppendLine(string.Join(',', Celda(f.Nombre), f.Ganadas, N(f.MontoGanado), f.Perdidas, N(f.TasaCierre), N(f.CicloDias),
                f.Interacciones, f.TareasHechas, f.LeadsConvertidos, N(f.Meta)));
        return sb.ToString();
    }

    /// <summary>Indicadores del resumen (etiqueta, valor sin formato regional).</summary>
    public static IEnumerable<(string Etiqueta, string Valor)> Resumen(ReporteVentas r)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(decimal? v) => v is { } x ? x.ToString("0.##", inv) : "";
        yield return (T("reportes.monto_ganado"), N(r.MontoGanado));
        yield return (T("reportes.ganadas"), r.Ganadas.ToString(inv));
        yield return (T("reportes.perdidas"), r.Perdidas.ToString(inv));
        yield return (T("reportes.tasa_cierre"), N(r.TasaCierre));
        yield return (T("reportes.ticket_promedio"), N(r.TicketPromedio));
        yield return (T("reportes.ciclo_dias"), N(r.CicloDias));
        yield return (T("reportes.leads_nuevos"), r.LeadsNuevos.ToString(inv));
        yield return (T("reportes.conversion_leads"), N(r.ConversionLeads));
        yield return (T("reportes.cotizaciones_enviadas"), r.CotizacionesEnviadas.ToString(inv));
        yield return (T("reportes.aceptacion_cotizaciones"), N(r.AceptacionCotizaciones));
        yield return (T("reportes.monto_abierto"), N(r.MontoAbierto));
        yield return (T("reportes.interacciones"), r.Interacciones.ToString(inv));
        yield return (T("reportes.tareas"), r.TareasHechas.ToString(inv));
    }
}
