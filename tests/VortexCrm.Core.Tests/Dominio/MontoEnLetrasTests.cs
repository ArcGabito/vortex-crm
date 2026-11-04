using VortexCrm.Core.Domain.Comun;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Comun;

public class MontoEnLetrasTests
{
    [Theory]
    [InlineData(0, "CERO")]
    [InlineData(1, "UNO")]
    [InlineData(15, "QUINCE")]
    [InlineData(16, "DIECISÉIS")]
    [InlineData(21, "VEINTIUNO")]
    [InlineData(22, "VEINTIDÓS")]
    [InlineData(30, "TREINTA")]
    [InlineData(45, "CUARENTA Y CINCO")]
    [InlineData(100, "CIEN")]
    [InlineData(101, "CIENTO UNO")]
    [InlineData(500, "QUINIENTOS")]
    [InlineData(999, "NOVECIENTOS NOVENTA Y NUEVE")]
    [InlineData(1000, "MIL")]
    [InlineData(1500, "MIL QUINIENTOS")]
    [InlineData(2001, "DOS MIL UNO")]
    [InlineData(21000, "VEINTIÚN MIL")]
    [InlineData(31000, "TREINTA Y UN MIL")]
    [InlineData(100000, "CIEN MIL")]
    [InlineData(101000, "CIENTO UN MIL")]
    [InlineData(1000000, "UN MILLÓN")]
    [InlineData(21000000, "VEINTIÚN MILLONES")]
    [InlineData(2345678, "DOS MILLONES TRESCIENTOS CUARENTA Y CINCO MIL SEISCIENTOS SETENTA Y OCHO")]
    [InlineData(1000000000, "MIL MILLONES")]
    public void EscribeElNumeroEnLetras(long numero, string esperado) =>
        Assert.Equal(esperado, MontoEnLetras.Numero(numero));

    [Theory]
    [InlineData(1500, "MIL QUINIENTOS CON 00/100 SOLES")]
    [InlineData(31500.5, "TREINTA Y UN MIL QUINIENTOS CON 50/100 SOLES")]
    [InlineData(0.99, "CERO CON 99/100 SOLES")]
    [InlineData(1.005, "UNO CON 01/100 SOLES")]
    public void SolesIncluyeLosCentimos(decimal monto, string esperado) =>
        Assert.Equal(esperado, MontoEnLetras.Soles(monto));
}
