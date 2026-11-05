using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

/// <summary>Base común de la suite en el CRM: impuesto y moneda del país, vendedor responsable, permisos y auditoría.</summary>
public sealed class SuiteBaseTests
{
    private static readonly Guid Otro = Guid.Parse("00000000-0000-0000-0000-000000000009");

    private static async Task WithAsync(TestWorkspace t, Func<Task> test)
    {
        await t.InitializeAsync();
        try
        {
            await test();
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task En_Mexico_la_cotizacion_va_en_pesos_con_IVA_16()
    {
        var t = new TestWorkspace(country: "MX", currency: "MXN");
        await WithAsync(t, async () =>
        {
            var cotizacion = await t.Crm.Cotizaciones.NuevaAsync(t.Customer);
            cotizacion.ModoIgv = ModoIgv.Adicional;
            cotizacion.AgregarLinea(new LineaCotizacion { Descripcion = "Consultoría", Unidad = "HORA", Cantidad = 10, PrecioUnitario = 100 });
            await t.Crm.Cotizaciones.GuardarAsync(cotizacion);

            var guardada = (await t.Crm.Cotizaciones.ObtenerAsync(cotizacion.Id))!;
            Assert.Equal("MXN", guardada.Moneda);
            Assert.Equal("IVA", guardada.NombreImpuesto);
            Assert.Equal(new TotalesCotizacion(1000m, 160m, 1160m), guardada.Totales);
            Assert.Equal(1160m, (await t.Crm.Cotizaciones.ListarAsync()).Single().Total);

            var (mensaje, _) = await t.Crm.Cotizaciones.WhatsAppAsync(cotizacion.Id);
            Assert.Contains("*Total: $ 1,160.00* (incluye IVA)", mensaje);
            using (VortexCrm.Core.Texts.CoreText.Use("en"))
            {
                var (enIngles, _) = await t.Crm.Cotizaciones.WhatsAppAsync(cotizacion.Id);
                Assert.Contains("*Total: $ 1,160.00* (includes IVA)", enIngles);
                Assert.Contains("here's quote", enIngles);
                Assert.NotEmpty(await t.Crm.Cotizaciones.GenerarPdfAsync(cotizacion.Id));
            }
            Assert.Equal("MIL CIENTO SESENTA CON 00/100 PESOS MEXICANOS", MontoEnLetras.Monto(1160m, "MXN"));
            Assert.Equal("NUEVE MIL NOVECIENTOS NOVENTA PESOS CHILENOS", MontoEnLetras.Monto(9990m, "CLP"));

            await t.Crm.Cotizaciones.CambiarEstadoAsync(cotizacion.Id, EstadoCotizacion.Aceptada);
            var accepted = (await t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.QuoteAccepted).Payload!.Value;
            Assert.Equal("MXN", accepted.GetProperty("currencyCode").GetString());
            Assert.Equal(1160m, accepted.GetProperty("total").GetDecimal());
        });
    }

    [Fact]
    public Task Un_vendedor_trabaja_lo_suyo_y_el_gerente_todo()
    {
        var t = new TestWorkspace(role: CrmPermissions.Seller);
        return WithAsync(t, async () =>
        {
            // Una oportunidad de otro vendedor (llegó por la réplica).
            t.Context.UserId = Otro;
            t.Context.UserName = "Rosa";
            var ajena = new Oportunidad { Titulo = "Cuenta de Rosa", ClienteId = t.Customer, Monto = 500 };
            await t.Crm.Oportunidades.GuardarAsync(ajena);
            Assert.Equal(Otro, (await t.Crm.Oportunidades.ObtenerAsync(ajena.Id))!.ResponsableId);

            t.Context.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            t.Context.UserName = "Luis";
            var propia = new Oportunidad { Titulo = "Cuenta de Luis", ClienteId = t.Customer, Monto = 300 };
            await t.Crm.Oportunidades.GuardarAsync(propia);
            await t.Crm.Oportunidades.CambiarEtapaAsync(propia.Id, EtapaOportunidad.Negociacion);

            var ex = await Assert.ThrowsAsync<CrmValidationException>(() => t.Crm.Oportunidades.CambiarEtapaAsync(ajena.Id, EtapaOportunidad.Ganado));
            Assert.Contains("Es de Rosa", ex.Message);
            await Assert.ThrowsAsync<CrmValidationException>(() => t.Crm.Oportunidades.EliminarAsync(propia.Id));
            await Assert.ThrowsAsync<CrmValidationException>(() => t.Crm.Clientes.GuardarAjustesAsync(new Ajustes(ModoIgv.Incluido, null)));

            t.Context.Role = CrmPermissions.SalesManager;
            await t.Crm.Oportunidades.CambiarEtapaAsync(ajena.Id, EtapaOportunidad.Ganado);
            await t.Crm.Oportunidades.EliminarAsync(propia.Id);

            t.Context.Role = CrmPermissions.ReadOnly;
            await Assert.ThrowsAsync<CrmValidationException>(() => t.Crm.Oportunidades.GuardarAsync(
                new Oportunidad { Titulo = "No", ClienteId = t.Customer }));
        });
    }

    [Fact]
    public Task Etapas_estados_ajustes_y_borrados_quedan_en_la_auditoria()
    {
        var t = new TestWorkspace();
        return WithAsync(t, async () =>
        {
            var oportunidad = new Oportunidad { Titulo = "Uniformes", ClienteId = t.Customer, Monto = 1000 };
            await t.Crm.Oportunidades.GuardarAsync(oportunidad);
            await t.Crm.Oportunidades.CambiarEtapaAsync(oportunidad.Id, EtapaOportunidad.Perdido, "Precio");
            var cotizacion = await t.Crm.Cotizaciones.NuevaAsync(t.Customer);
            cotizacion.AgregarLinea(new LineaCotizacion { Descripcion = "Polo", Cantidad = 1, PrecioUnitario = 30 });
            await t.Crm.Cotizaciones.GuardarAsync(cotizacion);
            await t.Crm.Cotizaciones.CambiarEstadoAsync(cotizacion.Id, EstadoCotizacion.Enviada);
            await t.Crm.Clientes.GuardarAjustesAsync(new Ajustes(ModoIgv.Adicional, "Pago a 30 días"));
            await t.Crm.Oportunidades.EliminarAsync(oportunidad.Id);

            var log = (await t.Crm.Audit.GetAsync(1, 20)).Items.Select(e => e.Action).Reverse();
            Assert.Equal(["stage.changed", "quote.enviada", "settings.changed", "record.deleted"], log);
        });
    }
}
