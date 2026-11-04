using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Contactos;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Contactos;

public class EntidadesTests
{
    [Fact]
    public void ContactoSinDniEsValido()
    {
        var contacto = new Contacto { Nombres = "Rosa", Apellidos = "Quispe" };

        Assert.Empty(contacto.Validar());
        Assert.Equal("Rosa Quispe", contacto.NombreCompleto);
    }

    [Fact]
    public void ContactoConDniMalFormadoONombreVacioTieneErrores()
    {
        var contacto = new Contacto { Dni = "123", Nombres = "" };

        Assert.Equal(2, contacto.Validar().Count);
    }

    [Fact]
    public void ClonarDevuelveUnaCopiaIndependiente()
    {
        var original = new Contacto { Nombres = "Rosa" };
        var copia = original.Clonar();

        copia.Nombres = "Lucía";

        Assert.Equal("Rosa", original.Nombres);
        Assert.Equal(original.Id, copia.Id);
    }

    [Fact]
    public void ElClienteDelHubMuestraSuNombreComercialYSuDireccionCompleta()
    {
        var cliente = new PartyRow(Guid.NewGuid(), "INVERSIONES ANDINA S.A.C.", "Andina", "Company", "RUC", "20131312955", true, false, null,
            null, "Lima", true, 1, false, "Jr. Cusco 245", "Cercado de Lima", "Lima");

        Assert.Equal("Andina", cliente.DisplayName);
        Assert.Equal("Jr. Cusco 245, Cercado de Lima, Lima", cliente.FullAddress);
        Assert.Null((cliente with { AddressLine = null, District = null, City = null, Region = null }).FullAddress);
    }
}
