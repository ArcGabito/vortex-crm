namespace VortexCrm.Core.Domain.Ventas;

public sealed record ResumenEtapa(EtapaOportunidad Etapa, int Cantidad, decimal Monto);

/// <summary>Totales del pipeline: cuánto hay en juego, cuánto se ganó y qué tan seguido se cierra.</summary>
public sealed record ResumenPipeline(
    IReadOnlyDictionary<EtapaOportunidad, ResumenEtapa> PorEtapa,
    int CantidadEnCurso,
    decimal MontoEnCurso,
    decimal? TasaDeCierre)
{
    public static ResumenPipeline Calcular(IEnumerable<Oportunidad> oportunidades)
    {
        var lista = oportunidades.ToList();

        var porEtapa = Etapas.Todas.ToDictionary(
            etapa => etapa,
            etapa =>
            {
                var deLaEtapa = lista.Where(o => o.Etapa == etapa).ToList();
                return new ResumenEtapa(etapa, deLaEtapa.Count, deLaEtapa.Sum(o => o.Monto));
            });

        var enCurso = porEtapa.Values.Where(r => r.Etapa.EstaAbierta()).ToList();
        var ganadas = porEtapa[EtapaOportunidad.Ganado].Cantidad;
        var cerradas = ganadas + porEtapa[EtapaOportunidad.Perdido].Cantidad;

        return new ResumenPipeline(
            porEtapa,
            enCurso.Sum(r => r.Cantidad),
            enCurso.Sum(r => r.Monto),
            cerradas == 0 ? null : (decimal)ganadas / cerradas);
    }
}
