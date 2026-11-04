using VortexCrm.Core.Domain.Contactos;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Contactos;

public class DocumentoIdentidadTests
{
    [Theory]
    [InlineData("20131312955")] // SUNAT
    [InlineData("20100070970")] // verificador 10 → 0
    [InlineData("10460278975")] // persona natural
    [InlineData("10123456781")] // verificador 11 → 1
    public void RucConDigitoVerificadorCorrectoEsValido(string ruc) =>
        Assert.True(DocumentoIdentidad.EsRucValido(ruc));

    [Theory]
    [InlineData("20131312954")] // dígito verificador incorrecto
    [InlineData("30131312955")] // prefijo inexistente
    [InlineData("2013131295")]  // 10 dígitos
    [InlineData("201313129555")] // 12 dígitos
    [InlineData("2013131295A")]
    [InlineData("")]
    [InlineData(null)]
    public void RucInvalidoSeRechaza(string? ruc) =>
        Assert.False(DocumentoIdentidad.EsRucValido(ruc));

    [Theory]
    [InlineData("46027897", true)]
    [InlineData("00000001", true)]
    [InlineData("4602789", false)]
    [InlineData("460278971", false)]
    [InlineData("4602789A", false)]
    [InlineData(null, false)]
    public void DniDebeTenerOchoDigitos(string? dni, bool esperado) =>
        Assert.Equal(esperado, DocumentoIdentidad.EsDniValido(dni));
}
