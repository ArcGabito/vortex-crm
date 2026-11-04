using VortexCrm.Core.Domain.Ventas;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Ventas;

public class VentasMensualesTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 4);

    private static Oportunidad Ganada(decimal monto, DateTimeOffset cierre)
    {
        var oportunidad = new Oportunidad { Titulo = "Venta", ContactoId = Guid.NewGuid(), Monto = monto };
        oportunidad.CambiarEtapa(EtapaOportunidad.Ganado, cierre);
        return oportunidad;
    }

    [Fact]
    public void DevuelveLosUltimosMesesEnOrdenYConCerosDondeNoHuboVentas()
    {
        var meses = VentasMensuales.Ultimos([], Hoy, 3);

        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], meses.Select(m => m.Mes));
        Assert.All(meses, m => Assert.Equal(0, m.Monto));
    }

    [Fact]
    public void SumaLasVentasGanadasEnElMesDeSuCierre()
    {
        var oportunidades = new[]
        {
            Ganada(100, new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)),
            Ganada(250, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)),
            Ganada(80, new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)),
        };

        var meses = VentasMensuales.Ultimos(oportunidades, Hoy, 2);

        Assert.Equal(new VentaMes(new DateOnly(2026, 9, 1), 80, 1), meses[0]);
        Assert.Equal(new VentaMes(new DateOnly(2026, 10, 1), 350, 2), meses[1]);
    }

    [Fact]
    public void ElMesSeMideEnHoraDePeru()
    {
        // 1 de octubre, 01:00 UTC, sigue siendo 30 de setiembre a las 8 p. m. en Lima
        var oportunidad = Ganada(100, new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));

        var meses = VentasMensuales.Ultimos([oportunidad], Hoy, 2);

        Assert.Equal(100, meses[0].Monto);
        Assert.Equal(0, meses[1].Monto);
    }

    [Fact]
    public void IgnoraLasOportunidadesQueNoEstanGanadas()
    {
        var perdida = new Oportunidad { Titulo = "Venta", ContactoId = Guid.NewGuid(), Monto = 500 };
        perdida.CambiarEtapa(EtapaOportunidad.Perdido, new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var abierta = new Oportunidad { Titulo = "Otra", ContactoId = Guid.NewGuid(), Monto = 300 };

        var meses = VentasMensuales.Ultimos([perdida, abierta], Hoy, 1);

        Assert.Equal(0, meses[0].Monto);
    }
}
