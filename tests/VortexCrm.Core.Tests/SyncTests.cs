using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    private readonly TestWorkspace _a = new("EQPA");
    private readonly TestWorkspace _b = new("EQPB", Guid.Parse("00000000-0000-0000-0000-000000000002"));

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    /// <summary>Lleva a <paramref name="destino"/> lo que <paramref name="origen"/> tiene por subir (y lo vacía).</summary>
    private static async Task ReplicarAsync(TestWorkspace origen, TestWorkspace destino)
    {
        foreach (var entry in await origen.OutboxAsync())
        {
            var e = TestWorkspace.AsEvent(entry, "CRM");
            if (entry.EventType == SuiteEvents.RecordUpserted)
                await destino.Crm.SyncHandlers.HandleRecordUpsertedAsync(e);
            else if (entry.EventType == SuiteEvents.RecordDeleted)
                await destino.Crm.SyncHandlers.HandleRecordDeletedAsync(e);
            await origen.Crm.SyncStore.RemoveAsync(entry.Id);
        }
    }

    [Fact]
    public async Task Lo_que_se_registra_en_un_equipo_aparece_en_el_otro()
    {
        var contacto = new Contacto { Nombres = "Rosa", Apellidos = "Quispe", ClienteId = _a.Customer };
        await _a.Crm.Contactos.GuardarAsync(contacto);
        var oportunidad = new Oportunidad { Titulo = "Uniformes 2027", ClienteId = _a.Customer, ContactoId = contacto.Id, Monto = 3000 };
        await _a.Crm.Oportunidades.GuardarAsync(oportunidad);
        var cotizacion = await _a.Crm.Cotizaciones.NuevaAsync(oportunidadId: oportunidad.Id);
        cotizacion.AgregarLinea(new LineaCotizacion { Descripcion = "Polo bordado", Cantidad = 50, PrecioUnitario = 30 });
        await _a.Crm.Cotizaciones.GuardarAsync(cotizacion);
        await _a.Crm.Clientes.GuardarAjustesAsync(new Ajustes(ModoIgv.Adicional, "Entrega en 7 días."));

        await ReplicarAsync(_a, _b);

        var ficha = (await _b.Crm.Clientes.FichaAsync(_b.Customer))!;
        Assert.Equal("Rosa Quispe", Assert.Single(ficha.Contactos).NombreCompleto);
        Assert.Equal("Uniformes 2027", Assert.Single(ficha.Oportunidades).Oportunidad.Titulo);
        var replica = Assert.Single(ficha.Cotizaciones);
        Assert.Equal("COT-EQPA-0001", replica.Codigo);
        Assert.Equal(1500m, replica.Total);
        Assert.Equal(ModoIgv.Adicional, (await _b.Crm.Clientes.ObtenerAjustesAsync()).ModoIgvPredeterminado);
        Assert.Empty(await _b.OutboxAsync());   // lo replicado no se vuelve a publicar
    }

    [Fact]
    public async Task El_ultimo_cambio_gana_y_los_eventos_repetidos_o_viejos_no_lo_pisan()
    {
        var oportunidad = new Oportunidad { Titulo = "Versión A", ClienteId = _a.Customer };
        await _a.Crm.Oportunidades.GuardarAsync(oportunidad);
        var viejo = (await _a.OutboxAsync()).Single();
        await ReplicarAsync(_a, _b);

        _b.Clock.Advance(TimeSpan.FromMinutes(5));
        var enB = (await _b.Crm.Oportunidades.ObtenerAsync(oportunidad.Id))!;
        enB.Titulo = "Versión B";
        await _b.Crm.Oportunidades.GuardarAsync(enB);
        await ReplicarAsync(_b, _a);

        Assert.Equal("Versión B", (await _a.Crm.Oportunidades.ObtenerAsync(oportunidad.Id))!.Titulo);

        // El evento viejo de A llega tarde a B (otro Id de evento, misma versión antigua): no pisa el cambio.
        await _b.Crm.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(viejo with { Id = Guid.NewGuid() }, "CRM"));
        Assert.Equal("Versión B", (await _b.Crm.Oportunidades.ObtenerAsync(oportunidad.Id))!.Titulo);
    }

    [Fact]
    public async Task Eliminar_se_replica_salvo_que_el_otro_equipo_lo_haya_editado_despues()
    {
        var a = new Contacto { Nombres = "Ana", ClienteId = _a.Customer };
        var b = new Contacto { Nombres = "Beto", ClienteId = _a.Customer };
        await _a.Crm.Contactos.GuardarAsync(a);
        await _a.Crm.Contactos.GuardarAsync(b);
        await ReplicarAsync(_a, _b);

        _b.Clock.Advance(TimeSpan.FromMinutes(5));
        var editado = (await _b.Crm.Contactos.ObtenerAsync(b.Id))!;
        editado.Cargo = "Gerente";
        await _b.Crm.Contactos.GuardarAsync(editado);

        await _a.Crm.Contactos.EliminarAsync(a.Id);
        await _a.Crm.Contactos.EliminarAsync(b.Id);
        await ReplicarAsync(_a, _b);

        Assert.Null(await _b.Crm.Contactos.ObtenerAsync(a.Id));
        Assert.Equal("Gerente", (await _b.Crm.Contactos.ObtenerAsync(b.Id))!.Cargo);
    }

    [Fact]
    public async Task Las_tareas_que_llegan_de_otro_equipo_programan_el_aviso_de_su_responsable()
    {
        var tarea = _b.Crm.Tareas.Nueva(_b.Customer);   // responsable: el usuario de B
        tarea.Titulo = "Enviar la cotización";
        tarea.Fecha = new DateOnly(2026, 11, 5);
        tarea.AvisarMinutosAntes = 0;
        // La crea A para B: mismo responsable, otro equipo.
        await _a.Crm.Tareas.GuardarAsync(tarea);
        Assert.Empty(_a.Avisos.Programados);

        await ReplicarAsync(_a, _b);
        await Task.Delay(50);   // el aviso se reprograma en segundo plano
        Assert.True(_b.Avisos.Programados.ContainsKey(tarea.Id));
    }

    [Fact]
    public async Task La_actividad_del_cliente_se_arma_con_lo_que_informan_POS_ERP_y_WMS()
    {
        var c = _a.Customer;
        await _a.Crm.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new { saleId = Guid.NewGuid(), number = "V-9AFE-000001", siteId = _a.Store, customerId = c, currencyCode = "PEN", total = 59.90m }, "POS"));
        await _a.Crm.SyncHandlers.HandleInvoiceIssuedAsync(TestWorkspace.Event(SuiteEvents.InvoiceIssued,
            new { invoiceId = Guid.NewGuid(), number = "F001-000045", customerId = c, total = 1180m, currencyCode = "PEN", kind = "factura" }, "ERP"));
        var payment = TestWorkspace.Event(SuiteEvents.PaymentReceived,
            new { paymentId = Guid.NewGuid(), customerId = c, amount = 500m, currencyCode = "PEN", method = "transfer", number = "COB-1" }, "ERP");
        await _a.Crm.SyncHandlers.HandlePaymentReceivedAsync(payment);
        await _a.Crm.SyncHandlers.HandlePaymentReceivedAsync(payment);
        await _a.Crm.SyncHandlers.HandleShipmentDispatchedAsync(TestWorkspace.Event(SuiteEvents.ShipmentDispatched,
            new { shipmentId = Guid.NewGuid(), orderId = Guid.NewGuid(), customerId = c, carrier = "Olva", trackingNumber = "OLV-1", number = "DES-1" }, "WMS"));
        await _a.Crm.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new { saleId = Guid.NewGuid(), siteId = _a.Store, total = 5m }, "POS"));   // sin cliente: no aplica

        var actividad = (await _a.Crm.Clientes.FichaAsync(c))!.Actividad;
        Assert.Equal(4, actividad.Count);
        Assert.Equal(["despacho", "cobro", "comprobante", "venta"], actividad.Select(x => x.Tipo).Reverse().Reverse());
        Assert.Contains(actividad, x => x.Titulo == "Factura F001-000045" && x.Monto == 1180m);
        Assert.Contains(actividad, x => x.Detalle == "Olva · guía OLV-1");
    }

    [Fact]
    public async Task La_anulacion_de_un_comprobante_queda_en_la_historia_del_cliente()
    {
        var c = _a.Customer;
        await _a.Crm.SyncHandlers.HandleInvoiceIssuedAsync(TestWorkspace.Event(SuiteEvents.InvoiceIssued,
            new { invoiceId = Guid.NewGuid(), number = "F001-000045", customerId = c, total = 1180m, currencyCode = "PEN", kind = "factura" }, "ERP"));
        var anulacion = TestWorkspace.Event(SuiteEvents.InvoiceVoided,
            new { invoiceId = Guid.NewGuid(), number = "F001-000045", reason = "RUC equivocado", customerId = c }, "ERP");
        await _a.Crm.SyncHandlers.HandleInvoiceVoidedAsync(anulacion);
        await _a.Crm.SyncHandlers.HandleInvoiceVoidedAsync(anulacion);   // repetido: una sola vez
        await _a.Crm.SyncHandlers.HandleInvoiceVoidedAsync(TestWorkspace.Event(SuiteEvents.InvoiceVoided,
            new { invoiceId = Guid.NewGuid(), number = "B001-000001", reason = "Error" }, "ERP"));   // sin cliente: no aplica

        var lista = (await _a.Crm.Clientes.FichaAsync(c))!.Actividad;
        Assert.Equal(2, lista.Count);
        Assert.Equal(0m, lista.Where(a => a.Tipo == "comprobante").Sum(a => a.Monto ?? 0));   // "Facturado" ya no lo cuenta
        var actividad = lista.Single(a => a.Monto < 0);
        Assert.Equal(-1180m, actividad.Monto);
        Assert.Equal("Comprobante F001-000045 anulado", actividad.Titulo);
        Assert.Equal("RUC equivocado", actividad.Detalle);
    }
}
