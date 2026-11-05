using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Etapas del pipeline de ventas, en el orden en que avanza una oportunidad.</summary>
public enum EtapaOportunidad
{
    Prospecto,
    Cotizado,
    Negociacion,
    Ganado,
    Perdido,
}

public static class Etapas
{
    public static IReadOnlyList<EtapaOportunidad> Todas { get; } = Enum.GetValues<EtapaOportunidad>();

    public static string Nombre(this EtapaOportunidad etapa) => T("enum.etapa." + etapa.ToString().ToLowerInvariant());

    /// <summary>Una oportunidad abierta todavía puede ganarse o perderse.</summary>
    public static bool EstaAbierta(this EtapaOportunidad etapa) =>
        etapa is EtapaOportunidad.Prospecto or EtapaOportunidad.Cotizado or EtapaOportunidad.Negociacion;
}
