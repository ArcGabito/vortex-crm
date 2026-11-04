using VortexCrm.Core.Domain.Comun;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Lo vendido (oportunidades ganadas) en un mes; <paramref name="Mes"/> es el primer día del mes.</summary>
public sealed record VentaMes(DateOnly Mes, decimal Monto, int Cantidad);

public static class VentasMensuales
{
    /// <summary>
    /// Las ventas ganadas de los últimos <paramref name="meses"/> meses, contando el actual, del más
    /// antiguo al más reciente. Los meses sin ventas aparecen en cero. El mes de cada venta es el de
    /// su cierre, en hora de Perú.
    /// </summary>
    public static IReadOnlyList<VentaMes> Ultimos(IEnumerable<Oportunidad> oportunidades, DateOnly hoy, int meses = 6)
    {
        var ganadas = oportunidades
            .Where(o => o.Etapa == EtapaOportunidad.Ganado && o.CerradoEn is not null)
            .Select(o =>
            {
                var cierre = DateOnly.FromDateTime(o.CerradoEn!.Value.ToOffset(FormatoPeru.ZonaHoraria).DateTime);
                return (Mes: new DateOnly(cierre.Year, cierre.Month, 1), o.Monto);
            })
            .ToList();

        var esteMes = new DateOnly(hoy.Year, hoy.Month, 1);

        return Enumerable.Range(0, meses)
            .Select(i => esteMes.AddMonths(i - (meses - 1)))
            .Select(mes =>
            {
                var delMes = ganadas.Where(g => g.Mes == mes).ToList();
                return new VentaMes(mes, delMes.Sum(g => g.Monto), delMes.Count);
            })
            .ToList();
    }
}
