using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Services;

namespace VortexCrm.Core.Tests;

public sealed class VentasTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<Cotizacion> CotizacionAsync(ModoIgv modo = ModoIgv.Incluido, Guid? oportunidadId = null, bool lineaLibre = false)
    {
        var cotizacion = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer, oportunidadId);
        cotizacion.ModoIgv = modo;
        var cafe = (await _t.Crm.Master.GetItemAsync(_t.Coffee))!;
        cotizacion.AgregarLinea(cafe.CrearLinea(cotizacion, 10));
        if (lineaLibre)
            cotizacion.AgregarLinea(new LineaCotizacion { Descripcion = "Instalación", Unidad = "SERV", Cantidad = 1, PrecioUnitario = 50 });
        await _t.Crm.Cotizaciones.GuardarAsync(cotizacion);
        return cotizacion;
    }

    [Fact]
    public async Task Guardar_numera_por_equipo_y_replica_la_cotizacion()
    {
        var cotizacion = await CotizacionAsync();

        Assert.Equal("COT-TEST-0001", cotizacion.Codigo);
        var guardada = (await _t.Crm.Cotizaciones.ObtenerAsync(cotizacion.Id))!;
        Assert.Single(guardada.Lineas);
        Assert.Equal(185m, guardada.Totales.Total);

        var e = Assert.Single(await _t.OutboxAsync());
        Assert.Equal(SuiteEvents.RecordUpserted, e.EventType);
        Assert.Equal(CrmRecordKinds.Cotizacion, e.Payload!.Value.GetProperty("kind").GetString());
        Assert.Equal("COT-TEST-0001", e.Payload!.Value.GetProperty("data").GetProperty("numero").GetString());
    }

    [Fact]
    public async Task Aceptar_gana_la_oportunidad_y_publica_quote_accepted_y_deal_won()
    {
        var oportunidad = new Oportunidad { Titulo = "Café para la oficina", ClienteId = _t.Customer };
        await _t.Crm.Oportunidades.GuardarAsync(oportunidad);
        var cotizacion = await CotizacionAsync(ModoIgv.Adicional, oportunidad.Id, lineaLibre: true);

        await _t.Crm.Cotizaciones.CambiarEstadoAsync(cotizacion.Id, EstadoCotizacion.Enviada);
        Assert.Equal(EtapaOportunidad.Cotizado, (await _t.Crm.Oportunidades.ObtenerAsync(oportunidad.Id))!.Etapa);
        await _t.Crm.Cotizaciones.CambiarEstadoAsync(cotizacion.Id, EstadoCotizacion.Aceptada);

        var ganada = (await _t.Crm.Oportunidades.ObtenerAsync(oportunidad.Id))!;
        Assert.Equal(EtapaOportunidad.Ganado, ganada.Etapa);
        // Más IGV: 10 × 15.68 (18.50 sin IGV) + 50 = 206.80 + 37.22 de IGV.
        Assert.Equal(244.02m, ganada.Monto);

        var outbox = await _t.OutboxAsync();
        var quote = outbox.Single(e => e.EventType == SuiteEvents.QuoteAccepted).Payload!.Value;
        Assert.Equal(_t.Customer, quote.GetProperty("customerId").GetGuid());
        Assert.Equal(244.02m, quote.GetProperty("total").GetDecimal());
        var linea = Assert.Single(quote.GetProperty("lines").EnumerateArray());   // la línea libre no va
        Assert.Equal(18.502m, linea.GetProperty("unitPrice").GetDecimal());        // 15.68 sin IGV + 18 %: precio con IGV, como en la suite

        var deal = outbox.Single(e => e.EventType == SuiteEvents.DealWon);
        Assert.Equal(cotizacion.Id, deal.Payload!.Value.GetProperty("quoteId").GetGuid());
        Assert.Equal(VortexCrm.Core.Integration.DeterministicId.From("deal_won", oportunidad.Id), deal.Id);

        cotizacion.Condiciones = "Otra";
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.GuardarAsync(cotizacion));
    }

    [Fact]
    public async Task El_pedido_se_genera_una_vez_con_el_id_que_calcula_el_ERP()
    {
        var conLineaLibre = await CotizacionAsync(lineaLibre: true);
        await _t.Crm.Cotizaciones.CambiarEstadoAsync(conLineaLibre.Id, EstadoCotizacion.Aceptada);
        var ex = await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.GenerarPedidoAsync(conLineaLibre.Id));
        Assert.Contains("artículos del catálogo", ex.Message);

        var cotizacion = await CotizacionAsync();
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.GenerarPedidoAsync(cotizacion.Id));
        await _t.Crm.Cotizaciones.CambiarEstadoAsync(cotizacion.Id, EstadoCotizacion.Aceptada);
        Assert.Equal("PED-TEST-0002", await _t.Crm.Cotizaciones.GenerarPedidoAsync(cotizacion.Id));
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.GenerarPedidoAsync(cotizacion.Id));

        var order = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.OrderCreated);
        // El mismo Id que OrderService.OrderIdForQuote del ERP: SHA-256 de "order_from_quote" + la cotización.
        Assert.Equal(ServicioCotizaciones.PedidoIdPara(cotizacion.Id), order.Id);
        Assert.Equal(cotizacion.Id, order.Payload!.Value.GetProperty("quoteId").GetGuid());
        Assert.Equal(_t.Store, order.Payload!.Value.GetProperty("siteId").GetGuid());
        Assert.Equal(order.Id, (await _t.Crm.Cotizaciones.ObtenerAsync(cotizacion.Id))!.PedidoId);
    }

    [Fact]
    public async Task El_PDF_y_el_mensaje_de_WhatsApp_van_a_nombre_del_cliente_y_su_contacto()
    {
        var contacto = new Contacto { Nombres = "Rosa", Apellidos = "Quispe", Telefono = "987 654 321", ClienteId = _t.Customer };
        await _t.Crm.Contactos.GuardarAsync(contacto);
        var cotizacion = await CotizacionAsync();
        cotizacion.ContactoId = contacto.Id;
        await _t.Crm.Cotizaciones.GuardarAsync(cotizacion);

        var (mensaje, enlace) = await _t.Crm.Cotizaciones.WhatsAppAsync(cotizacion.Id);
        Assert.StartsWith("Hola Rosa,", mensaje);
        Assert.StartsWith("https://wa.me/51987654321?text=", enlace);

        var pdf = await _t.Crm.Cotizaciones.GenerarPdfAsync(cotizacion.Id);
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task Las_tareas_avisan_solo_a_su_responsable()
    {
        var mia = _t.Crm.Tareas.Nueva(_t.Customer);
        mia.Titulo = "Llamar para cerrar";
        mia.Fecha = new DateOnly(2026, 11, 3);
        mia.Hora = new TimeOnly(10, 0);
        mia.AvisarMinutosAntes = 15;
        await _t.Crm.Tareas.GuardarAsync(mia);
        Assert.Equal("Luis", mia.ResponsableNombre);
        Assert.True(_t.Avisos.Programados.ContainsKey(mia.Id));

        var deOtro = _t.Crm.Tareas.Nueva();
        deOtro.Titulo = "Visitar al cliente";
        deOtro.Fecha = new DateOnly(2026, 11, 4);
        deOtro.AvisarMinutosAntes = 0;
        deOtro.ResponsableId = Guid.NewGuid();
        await _t.Crm.Tareas.GuardarAsync(deOtro);
        Assert.False(_t.Avisos.Programados.ContainsKey(deOtro.Id));
        Assert.Single(await _t.Crm.Tareas.ListarAsync(soloMias: true));

        await _t.Crm.Tareas.CompletarAsync(mia.Id);
        Assert.Empty(_t.Avisos.Programados);
    }

    [Fact]
    public async Task Los_ajustes_dan_el_igv_y_las_condiciones_de_las_cotizaciones_nuevas()
    {
        await _t.Crm.Clientes.GuardarAjustesAsync(new Ajustes(ModoIgv.NoAplica, "Pago al contado."));
        var nueva = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer);

        Assert.Equal(ModoIgv.NoAplica, nueva.ModoIgv);
        Assert.Equal("Pago al contado.", nueva.Condiciones);
        Assert.Equal(_t.Store, nueva.SedeId);
        Assert.Equal(new DateOnly(2026, 11, 17), nueva.ValidaHasta);
    }

    [Fact]
    public async Task El_panel_resume_pipeline_tareas_y_cotizaciones_abiertas()
    {
        await _t.Crm.Oportunidades.GuardarAsync(new Oportunidad { Titulo = "A", ClienteId = _t.Customer, Monto = 1000 });
        var b = new Oportunidad { Titulo = "B", ClienteId = _t.Customer, Monto = 500 };
        await _t.Crm.Oportunidades.GuardarAsync(b);
        await _t.Crm.Oportunidades.CambiarEtapaAsync(b.Id, EtapaOportunidad.Ganado);
        await CotizacionAsync();

        var panel = await _t.Crm.Clientes.PanelAsync();
        Assert.Equal(1000m, panel.Pipeline.MontoEnCurso);
        Assert.Equal(500m, panel.GanadoEsteMes);
        Assert.Equal(1, panel.CotizacionesAbiertas);
        Assert.Equal(185m, panel.MontoCotizado);
    }
}
