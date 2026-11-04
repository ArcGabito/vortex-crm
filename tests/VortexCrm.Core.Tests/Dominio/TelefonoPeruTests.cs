using VortexCrm.Core.Domain.Comun;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Comun;

public class TelefonoPeruTests
{
    [Theory]
    [InlineData("987654321", "51987654321")]
    [InlineData("987 654 321", "51987654321")]
    [InlineData("+51 987 654 321", "51987654321")]
    [InlineData("51987654321", "51987654321")]
    [InlineData("+34 612 345 678", "34612345678")] // España
    public void ConvierteCelularesAlFormatoDeWhatsApp(string telefono, string esperado) =>
        Assert.Equal(esperado, TelefonoPeru.ParaWhatsApp(telefono));

    [Theory]
    [InlineData("(01) 315-3300")] // fijo de Lima
    [InlineData("12345")]
    [InlineData("")]
    [InlineData(null)]
    public void LoQueNoEsCelularDevuelveNull(string? telefono) =>
        Assert.Null(TelefonoPeru.ParaWhatsApp(telefono));
}
