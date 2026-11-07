using VortexCrm.Core.Domain.Ventas;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Ventas;

public class TableroVentasTests
{
    private static readonly DateOnly Hoy = new(2026, 11, 6);

    private static Oportunidad Op(string titulo, decimal monto, EtapaOportunidad etapa = EtapaOportunidad.Prospecto, DateTimeOffset? cierre = null)
    {
        var o = new Oportunidad { Titulo = titulo, ContactoId = Guid.NewGuid(), Monto = monto };
        if (etapa != EtapaOportunidad.Prospecto)
            o.CambiarEtapa(etapa, cierre ?? DateTimeOffset.UtcNow);
        return o;
    }

    private static DateTimeOffset Dia(int mes, int dia) => new(2026, mes, dia, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CurvaDeGanadoYCotizadoPorMesSinBorradores()
    {
        var oportunidades = new[]
        {
            Op("A", 1000, EtapaOportunidad.Ganado, Dia(10, 20)),
            Op("B", 3000, EtapaOportunidad.Ganado, Dia(11, 2)),
            Op("C", 500, EtapaOportunidad.Ganado, Dia(11, 4)),
            Op("D", 900, EtapaOportunidad.Perdido, Dia(11, 5)),
        };
        var cotizaciones = new[]
        {
            (new DateOnly(2026, 11, 1), 1200m, EstadoCotizacion.Enviada),
            (new DateOnly(2026, 11, 3), 800m, EstadoCotizacion.Aceptada),
            (new DateOnly(2026, 11, 4), 5000m, EstadoCotizacion.Borrador),
            (new DateOnly(2026, 10, 9), 700m, EstadoCotizacion.Rechazada),
        };

        var t = TableroVentas.Calcular(oportunidades, cotizaciones, Hoy, meses: 3);

        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)], t.Tendencia.Select(p => p.Mes));
        Assert.Equal([0m, 1000m, 3500m], t.Tendencia.Select(p => p.Ganado));
        Assert.Equal([0m, 700m, 2000m], t.Tendencia.Select(p => p.Cotizado));
        Assert.Equal(3500m, t.GanadoEsteMes);
        Assert.Equal(250m, t.CambioGanadoPct);          // 1000 → 3500
        Assert.Equal(1750m, t.TicketPromedio);          // 3500 / 2
        Assert.Equal(75m, t.CambioTicketPct);           // 1000 → 1750
    }

    [Fact]
    public void SinVentasElMesPasadoNoHayVariacion()
    {
        var t = TableroVentas.Calcular([Op("A", 100, EtapaOportunidad.Ganado, Dia(11, 1))], [], Hoy, meses: 2);
        Assert.Null(t.CambioGanadoPct);
        Assert.Null(t.CambioTicketPct);
    }

    [Fact]
    public void PrincipalesSonLasAbiertasDeMayorMonto()
    {
        var oportunidades = new[]
        {
            Op("Chica", 100), Op("Grande", 9000, EtapaOportunidad.Negociacion), Op("Mediana", 2000, EtapaOportunidad.Cotizado),
            Op("Ganada", 50000, EtapaOportunidad.Ganado, Dia(11, 1)), Op("Otra", 300),
        };

        var t = TableroVentas.Calcular(oportunidades, [], Hoy, principales: 3);

        Assert.Equal(["Grande", "Mediana", "Otra"], t.Principales.Select(o => o.Titulo));
    }
}
