using VortexCrm.Core.Domain.Comun;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Comun;

public class FormatoPeruTests
{
    [Theory]
    [InlineData(1500, "S/ 1,500.00")]
    [InlineData(0.5, "S/ 0.50")]
    [InlineData(1234567.891, "S/ 1,234,567.89")]
    [InlineData(-20, "S/ -20.00")]
    public void SolesUsaElFormatoPeruano(decimal monto, string esperado) =>
        Assert.Equal(esperado, FormatoPeru.Soles(monto));

    [Theory]
    [InlineData(0.75, "75%")]
    [InlineData(1, "100%")]
    public void PorcentajeSinEspacio(decimal valor, string esperado) =>
        Assert.Equal(esperado, valor.ToString("P0", FormatoPeru.Cultura));

    [Fact]
    public void HoyUsaLaHoraDePeruYNoLaUtc()
    {
        // 1 de octubre a las 03:00 UTC todavía es 30 de setiembre en Lima (UTC-5)
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 9, 30), FormatoPeru.Hoy(reloj));
    }

    [Theory]
    [InlineData(9, 0, "9:00 a. m.")]
    [InlineData(13, 5, "1:05 p. m.")]
    [InlineData(0, 30, "12:30 a. m.")]
    public void HoraEnFormatoDeDoceHoras(int hora, int minuto, string esperado) =>
        Assert.Equal(esperado, FormatoPeru.Hora(new TimeOnly(hora, minuto)));

    [Theory]
    [InlineData(0, "Hoy")]
    [InlineData(1, "Mañana")]
    [InlineData(-1, "Ayer")]
    public void FechaRelativaCercana(int dias, string esperado)
    {
        var hoy = new DateOnly(2026, 10, 2);
        Assert.Equal(esperado, FormatoPeru.FechaRelativa(hoy.AddDays(dias), hoy));
    }

    [Fact]
    public void FechaRelativaLejanaMuestraLaFechaYElAñoSiEsOtro()
    {
        var hoy = new DateOnly(2026, 10, 2);

        Assert.Contains("5", FormatoPeru.FechaRelativa(new DateOnly(2026, 10, 5), hoy));
        Assert.DoesNotContain("2026", FormatoPeru.FechaRelativa(new DateOnly(2026, 10, 5), hoy));
        Assert.EndsWith("2027", FormatoPeru.FechaRelativa(new DateOnly(2027, 1, 15), hoy));
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
