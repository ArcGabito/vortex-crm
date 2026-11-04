using VortexCrm.Core.Domain.Ventas;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Ventas;

public class ResumenPipelineTests
{
    private static Oportunidad Crear(EtapaOportunidad etapa, decimal monto)
    {
        var oportunidad = new Oportunidad { Monto = monto };
        oportunidad.CambiarEtapa(etapa, DateTimeOffset.UtcNow);
        return oportunidad;
    }

    [Fact]
    public void SumaPorEtapaYSoloCuentaLasAbiertasComoEnCurso()
    {
        var resumen = ResumenPipeline.Calcular(
        [
            Crear(EtapaOportunidad.Prospecto, 1000),
            Crear(EtapaOportunidad.Prospecto, 500),
            Crear(EtapaOportunidad.Negociacion, 2500.50m),
            Crear(EtapaOportunidad.Ganado, 4000),
            Crear(EtapaOportunidad.Perdido, 900),
        ]);

        Assert.Equal(2, resumen.PorEtapa[EtapaOportunidad.Prospecto].Cantidad);
        Assert.Equal(1500, resumen.PorEtapa[EtapaOportunidad.Prospecto].Monto);
        Assert.Equal(0, resumen.PorEtapa[EtapaOportunidad.Cotizado].Cantidad);
        Assert.Equal(3, resumen.CantidadEnCurso);
        Assert.Equal(4000.50m, resumen.MontoEnCurso);
    }

    [Fact]
    public void LaTasaDeCierreEsGanadasSobreCerradas()
    {
        var resumen = ResumenPipeline.Calcular(
        [
            Crear(EtapaOportunidad.Ganado, 100),
            Crear(EtapaOportunidad.Ganado, 100),
            Crear(EtapaOportunidad.Ganado, 100),
            Crear(EtapaOportunidad.Perdido, 100),
            Crear(EtapaOportunidad.Prospecto, 100), // las abiertas no cuentan
        ]);

        Assert.Equal(0.75m, resumen.TasaDeCierre);
    }

    [Fact]
    public void SinOportunidadesCerradasNoHayTasaDeCierre()
    {
        var resumen = ResumenPipeline.Calcular([Crear(EtapaOportunidad.Cotizado, 100)]);

        Assert.Null(resumen.TasaDeCierre);
        Assert.Equal(Etapas.Todas.Count, resumen.PorEtapa.Count);
    }
}
