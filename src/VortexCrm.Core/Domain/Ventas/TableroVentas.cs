using VortexCrm.Core.Domain.Comun;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Un mes de la curva del panel: lo ganado (por fecha de cierre) y lo cotizado (por fecha de emisión).</summary>
public sealed record PuntoVentas(DateOnly Mes, decimal Ganado, int Ganadas, decimal Cotizado, int Cotizaciones)
{
    public decimal TicketPromedio => Ganadas == 0 ? 0 : Math.Round(Ganado / Ganadas, 2);
}

/// <summary>
/// Indicadores del panel del CRM. Las variaciones comparan el mes en curso con el mes pasado completo; son null si el
/// mes pasado fue cero.
/// </summary>
public sealed record TableroVentas(
    IReadOnlyList<PuntoVentas> Tendencia,
    decimal GanadoEsteMes,
    decimal? CambioGanadoPct,
    decimal TicketPromedio,
    decimal? CambioTicketPct,
    IReadOnlyList<Oportunidad> Principales)
{
    /// <summary>
    /// Arma el tablero de los últimos <paramref name="meses"/> meses (incluido el actual). Lo cotizado cuenta las
    /// cotizaciones que salieron al cliente (enviadas, aceptadas o rechazadas), no los borradores.
    /// <paramref name="principales"/>: oportunidades abiertas de mayor monto.
    /// </summary>
    public static TableroVentas Calcular(IEnumerable<Oportunidad> oportunidades,
        IEnumerable<(DateOnly Emision, decimal Total, EstadoCotizacion Estado)> cotizaciones, DateOnly hoy, int meses = 7,
        int principales = 4)
    {
        var lista = oportunidades.ToList();
        var esteMes = new DateOnly(hoy.Year, hoy.Month, 1);
        var desde = esteMes.AddMonths(-(meses - 1));

        var ganadas = lista
            .Where(o => o.Etapa == EtapaOportunidad.Ganado && o.CerradoEn is not null)
            .Select(o => (Mes: MesDe(DateOnly.FromDateTime(o.CerradoEn!.Value.ToOffset(FormatoPeru.ZonaHoraria).DateTime)), o.Monto))
            .ToList();
        var cotizadas = cotizaciones
            .Where(q => q.Estado != EstadoCotizacion.Borrador)
            .Select(q => (Mes: MesDe(q.Emision), q.Total))
            .ToList();

        var tendencia = Enumerable.Range(0, meses).Select(i => desde.AddMonths(i)).Select(mes => new PuntoVentas(
            mes,
            ganadas.Where(g => g.Mes == mes).Sum(g => g.Monto), ganadas.Count(g => g.Mes == mes),
            cotizadas.Where(q => q.Mes == mes).Sum(q => q.Total), cotizadas.Count(q => q.Mes == mes))).ToList();

        var actual = tendencia[^1];
        var anterior = tendencia.Count > 1 ? tendencia[^2] : null;
        return new TableroVentas(
            tendencia,
            actual.Ganado, Cambio(actual.Ganado, anterior?.Ganado),
            actual.TicketPromedio, Cambio(actual.TicketPromedio, anterior?.TicketPromedio),
            lista.Where(o => o.Etapa.EstaAbierta()).OrderByDescending(o => o.Monto).ThenBy(o => o.Titulo).Take(principales).ToList());
    }

    private static DateOnly MesDe(DateOnly dia) => new(dia.Year, dia.Month, 1);

    private static decimal? Cambio(decimal actual, decimal? anterior) =>
        anterior is > 0 ? Math.Round((actual - anterior.Value) / anterior.Value * 100, 1) : null;
}
