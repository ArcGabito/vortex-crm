using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>
/// Una etapa abierta de un embudo. <see cref="Equivale"/> la ubica en el ciclo común (Prospecto, Cotizado o Negociación),
/// para que las cotizaciones hagan avanzar la oportunidad y los reportes sumen igual en todos los embudos. Ganado y
/// Perdido son comunes a todos los embudos.
/// </summary>
public sealed record EtapaEmbudo(Guid Id, string Nombre, EtapaOportunidad Equivale, int Probabilidad, string? Color = null);

/// <summary>
/// Embudo de ventas configurable por línea de negocio (por ejemplo "Proyectos" y "Recurrentes"), con sus etapas en orden.
/// Se replica entre equipos como registro del CRM. Sin embudos configurados se usa <see cref="Predeterminado"/>.
/// </summary>
public sealed record Embudo(Guid Id, string Nombre, IReadOnlyList<EtapaEmbudo> Etapas, bool PorDefecto = false, bool Archivado = false)
{
    /// <summary>El embudo de siempre: las tres etapas abiertas con su probabilidad habitual.</summary>
    public static Embudo Predeterminado { get; } = new(Guid.Empty, "", [
        new(EtapaPredeterminada(EtapaOportunidad.Prospecto), "", EtapaOportunidad.Prospecto, 10),
        new(EtapaPredeterminada(EtapaOportunidad.Cotizado), "", EtapaOportunidad.Cotizado, 40),
        new(EtapaPredeterminada(EtapaOportunidad.Negociacion), "", EtapaOportunidad.Negociacion, 70),
    ], PorDefecto: true);

    public bool EsPredeterminado => Id == Guid.Empty;

    /// <summary>Id fijo de las etapas del embudo predeterminado, el mismo en todos los equipos.</summary>
    public static Guid EtapaPredeterminada(EtapaOportunidad etapa) => new($"00000000-0000-0000-0000-00000000000{(int)etapa + 1}");

    public EtapaEmbudo? Etapa(Guid? id) => id is { } e ? Etapas.FirstOrDefault(x => x.Id == e) : null;

    /// <summary>La primera etapa del embudo que equivale a la etapa común (la oportunidad entra o avanza ahí).</summary>
    public EtapaEmbudo? PrimeraQueEquivale(EtapaOportunidad etapa) => Etapas.FirstOrDefault(x => x.Equivale == etapa);

    /// <summary>Nombre que se muestra: el propio o, en el predeterminado, el de la etapa común.</summary>
    public static string NombreDe(EtapaEmbudo etapa) => etapa.Nombre is { Length: > 0 } n ? n : etapa.Equivale.Nombre();

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Nombre))
            errores.Add(T("error.el_embudo_necesita_nombre"));
        if (Etapas.Count == 0)
            errores.Add(T("error.el_embudo_necesita_etapas"));
        if (Etapas.Any(e => string.IsNullOrWhiteSpace(e.Nombre)))
            errores.Add(T("error.cada_etapa_necesita_nombre"));
        if (Etapas.Any(e => !e.Equivale.EstaAbierta()))
            errores.Add(T("error.las_etapas_son_abiertas"));
        if (Etapas.Any(e => e.Probabilidad is < 0 or > 100))
            errores.Add(T("error.probabilidad_entre_0_y_100"));
        if (Etapas.Select(e => e.Id).Distinct().Count() != Etapas.Count)
            errores.Add(T("error.etapas_repetidas"));
        // El orden sigue el ciclo: una etapa no puede equivaler a algo anterior a la que la precede.
        if (Etapas.Zip(Etapas.Skip(1)).Any(p => p.Second.Equivale < p.First.Equivale))
            errores.Add(T("error.etapas_en_orden"));
        return errores;
    }
}

/// <summary>Probabilidad de cierre de una oportunidad según su etapa (100 % ganada, 0 % perdida).</summary>
public static class Probabilidades
{
    public static int De(Oportunidad o, Embudo embudo) => o.Etapa switch
    {
        EtapaOportunidad.Ganado => 100,
        EtapaOportunidad.Perdido => 0,
        _ => (embudo.Etapa(o.EtapaId) ?? embudo.PrimeraQueEquivale(o.Etapa) ?? Embudo.Predeterminado.PrimeraQueEquivale(o.Etapa))!.Probabilidad,
    };
}

/// <summary>Meta de ventas (lo ganado) de un mes, de un vendedor o, sin vendedor, del equipo completo.</summary>
public sealed record Meta(Guid Id, DateOnly Mes, Guid? ResponsableId, string? ResponsableNombre, decimal Monto);

/// <summary>Un mes del pronóstico: lo ganado, lo que se espera ganar (ponderado por probabilidad) y la meta.</summary>
public sealed record PronosticoMes(DateOnly Mes, decimal Ganado, decimal EnCurso, decimal Ponderado, int Abiertas, decimal? Meta)
{
    /// <summary>Lo ganado más lo ponderado: lo que se espera cerrar al terminar el mes.</summary>
    public decimal Esperado => Ganado + Ponderado;

    public decimal? AvancePct => Meta is > 0 ? Math.Round(Ganado / Meta.Value * 100, 1) : null;
}

/// <summary>Avance de un vendedor en un mes: lo ganado contra su meta, y lo ponderado que tiene abierto.</summary>
public sealed record PronosticoVendedor(Guid? ResponsableId, string Nombre, decimal Ganado, decimal Ponderado, int Abiertas, decimal? Meta)
{
    public decimal? AvancePct => Meta is > 0 ? Math.Round(Ganado / Meta.Value * 100, 1) : null;
}

public sealed record Pronostico(IReadOnlyList<PronosticoMes> Meses, IReadOnlyList<PronosticoVendedor> Vendedores, decimal SinFecha)
{
    /// <summary>
    /// Pronóstico desde el mes de <paramref name="hoy"/>: lo ganado por mes de cierre y lo abierto por mes de cierre
    /// estimado, ponderado por la probabilidad de su etapa. Lo abierto con fecha ya pasada cuenta en el mes actual (debería
    /// cerrarse ya); lo abierto sin fecha va aparte. El detalle por vendedor es del mes actual.
    /// </summary>
    public static Pronostico Calcular(IEnumerable<Oportunidad> oportunidades, IReadOnlyList<Embudo> embudos, IEnumerable<Meta> metas,
        DateOnly hoy, int meses = 3)
    {
        var lista = oportunidades.ToList();
        var metasList = metas.ToList();
        var actual = new DateOnly(hoy.Year, hoy.Month, 1);
        Embudo EmbudoDe(Oportunidad o) => embudos.FirstOrDefault(e => e.Id == o.EmbudoId) ?? Embudo.Predeterminado;
        DateOnly? MesCierre(Oportunidad o) => o.Etapa == EtapaOportunidad.Ganado
            ? o.CerradoEn is { } c ? Mes(DateOnly.FromDateTime(c.ToOffset(Comun.FormatoPeru.ZonaHoraria).DateTime)) : null
            : o.FechaCierreEstimada is { } f ? (Mes(f) < actual ? actual : Mes(f)) : null;

        var abiertas = lista.Where(o => o.Etapa.EstaAbierta()).ToList();
        var ganadas = lista.Where(o => o.Etapa == EtapaOportunidad.Ganado).ToList();

        var filas = Enumerable.Range(0, meses).Select(i => actual.AddMonths(i)).Select(mes =>
        {
            var abiertasMes = abiertas.Where(o => MesCierre(o) == mes).ToList();
            return new PronosticoMes(mes,
                ganadas.Where(o => MesCierre(o) == mes).Sum(o => o.Monto),
                abiertasMes.Sum(o => o.Monto),
                Math.Round(abiertasMes.Sum(o => o.Monto * Probabilidades.De(o, EmbudoDe(o)) / 100m), 2),
                abiertasMes.Count,
                metasList.Where(m => m.Mes == mes && m.ResponsableId is null).Select(m => (decimal?)m.Monto).FirstOrDefault());
        }).ToList();

        var vendedores = lista
            .Where(o => (o.Etapa == EtapaOportunidad.Ganado || o.Etapa.EstaAbierta()) && MesCierre(o) == actual)
            .Select(o => (o.ResponsableId, Nombre: o.ResponsableNombre ?? ""))
            .Concat(metasList.Where(m => m.Mes == actual && m.ResponsableId is not null).Select(m => (m.ResponsableId, Nombre: m.ResponsableNombre ?? "")))
            .GroupBy(v => v.ResponsableId)
            .Select(g =>
            {
                var suyas = lista.Where(o => o.ResponsableId == g.Key && MesCierre(o) == actual).ToList();
                var abiertasSuyas = suyas.Where(o => o.Etapa.EstaAbierta()).ToList();
                return new PronosticoVendedor(g.Key, g.Select(v => v.Nombre).FirstOrDefault(n => n.Length > 0) ?? "",
                    suyas.Where(o => o.Etapa == EtapaOportunidad.Ganado).Sum(o => o.Monto),
                    Math.Round(abiertasSuyas.Sum(o => o.Monto * Probabilidades.De(o, EmbudoDe(o)) / 100m), 2), abiertasSuyas.Count,
                    metasList.Where(m => m.Mes == actual && m.ResponsableId == g.Key).Select(m => (decimal?)m.Monto).FirstOrDefault());
            })
            .OrderByDescending(v => v.Ganado).ThenByDescending(v => v.Ponderado)
            .ToList();

        return new Pronostico(filas, vendedores, abiertas.Where(o => o.FechaCierreEstimada is null).Sum(o => o.Monto));
    }

    public static DateOnly Mes(DateOnly dia) => new(dia.Year, dia.Month, 1);
}
