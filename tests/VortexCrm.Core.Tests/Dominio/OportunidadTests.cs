using VortexCrm.Core.Domain.Ventas;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Ventas;

public class OportunidadTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnaOportunidadNuevaEmpiezaComoProspectoAbierto()
    {
        var oportunidad = new Oportunidad();

        Assert.Equal(EtapaOportunidad.Prospecto, oportunidad.Etapa);
        Assert.Null(oportunidad.CerradoEn);
    }

    [Theory]
    [InlineData(EtapaOportunidad.Ganado)]
    [InlineData(EtapaOportunidad.Perdido)]
    public void PasarAGanadoOPerdidoRegistraLaFechaDeCierre(EtapaOportunidad etapa)
    {
        var oportunidad = new Oportunidad();

        oportunidad.CambiarEtapa(etapa, Ahora);

        Assert.Equal(etapa, oportunidad.Etapa);
        Assert.Equal(Ahora, oportunidad.CerradoEn);
    }

    [Fact]
    public void ReabrirUnaOportunidadPerdidaBorraElCierreYElMotivo()
    {
        var oportunidad = new Oportunidad();
        oportunidad.CambiarEtapa(EtapaOportunidad.Perdido, Ahora);
        oportunidad.MotivoPerdida = "Eligió otro proveedor";

        oportunidad.CambiarEtapa(EtapaOportunidad.Negociacion, Ahora.AddDays(3));

        Assert.Null(oportunidad.CerradoEn);
        Assert.Null(oportunidad.MotivoPerdida);
    }

    [Fact]
    public void CambiarALaMismaEtapaNoTocaLaFechaDeCierre()
    {
        var oportunidad = new Oportunidad();
        oportunidad.CambiarEtapa(EtapaOportunidad.Ganado, Ahora);

        oportunidad.CambiarEtapa(EtapaOportunidad.Ganado, Ahora.AddDays(5));

        Assert.Equal(Ahora, oportunidad.CerradoEn);
    }

    [Fact]
    public void SoloEstaVencidaSiSigueAbiertaYPasoLaFecha()
    {
        var hoy = new DateOnly(2026, 10, 1);
        var oportunidad = new Oportunidad { FechaCierreEstimada = new DateOnly(2026, 9, 30) };

        Assert.True(oportunidad.EstaVencida(hoy));
        Assert.False(oportunidad.EstaVencida(new DateOnly(2026, 9, 30)));

        oportunidad.CambiarEtapa(EtapaOportunidad.Ganado, Ahora);
        Assert.False(oportunidad.EstaVencida(hoy));

        Assert.False(new Oportunidad().EstaVencida(hoy));
    }

    [Fact]
    public void ValidarExigeTituloClienteYMontoNoNegativo()
    {
        var oportunidad = new Oportunidad { Monto = -1 };

        Assert.Equal(3, oportunidad.Validar().Count);

        oportunidad.Titulo = "50 polos";
        oportunidad.ClienteId = Guid.NewGuid();
        oportunidad.Monto = 1500;
        Assert.Empty(oportunidad.Validar());
    }

    [Fact]
    public void NegociacionSeMuestraConTilde() =>
        Assert.Equal("Negociación", EtapaOportunidad.Negociacion.Nombre());
}
