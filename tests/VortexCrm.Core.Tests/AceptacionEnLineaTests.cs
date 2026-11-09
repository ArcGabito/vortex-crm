using VortexApi.Client;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 4b: el cliente acepta la cotización desde el enlace público del hub y el pedido llega al ERP.</summary>
public sealed class AceptacionEnLineaTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<Cotizacion> CotizacionAsync()
    {
        var q = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer);
        var cafe = (await _t.Crm.Master.GetItemAsync(_t.Coffee))!;
        q.AgregarLinea(cafe.CrearLinea(q, 10));
        q.AgregarLinea(new LineaCotizacion { Descripcion = "Instalación", Unidad = "SERV", Cantidad = 1, PrecioUnitario = 50, Opcional = true });
        q.Condiciones = "Pago a 30 días";
        await _t.Crm.Cotizaciones.GuardarAsync(q);
        return q;
    }

    [Fact]
    public async Task El_enlace_lleva_la_cotizacion_y_al_aceptarla_queda_aceptada_con_su_pedido()
    {
        var q = await CotizacionAsync();
        var url = await _t.Crm.Cotizaciones.EnlaceParaAceptarAsync(q.Id);

        // Compartir el enlace la deja enviada; la copia del hub es lo que ve el cliente.
        var guardada = (await _t.Crm.Cotizaciones.ObtenerAsync(q.Id))!;
        Assert.Equal((EstadoCotizacion.Enviada, url), (guardada.Estado, guardada.EnlaceUrl));
        var (link, pedido) = _t.Context.Hub!.PorDocumento[q.Id];
        Assert.Equal(("quote", "COT-TEST-0001", "PEN", "es"), (pedido.DocumentType, pedido.Document.Number, pedido.Document.Currency, pedido.Language));
        Assert.Equal([false, true], pedido.Document.Lines.Select(l => l.Optional));
        Assert.Equal(185m, pedido.Document.Totals.Single(t => t.Grand).Amount);
        Assert.Equal(("Bodega Don Pepe", "Pago a 30 días"), (pedido.Document.Customer!.Name, pedido.Document.Notes));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.FromHours(-5)), pedido.ExpiresAt);   // fin del día de validez
        Assert.Contains(url, (await _t.Crm.Cotizaciones.WhatsAppAsync(q.Id)).Mensaje);

        // Pedirlo otra vez (tras editar) da la misma URL.
        Assert.Equal(url, await _t.Crm.Cotizaciones.EnlaceParaAceptarAsync(q.Id));

        await _t.Crm.SyncHandlers.HandlePublicLinkRespondedAsync(_t.Context.Hub.Responder(q.Id, aceptar: true, comentario: "Conforme"));

        var aceptada = (await _t.Crm.Cotizaciones.ObtenerAsync(q.Id))!;
        Assert.Equal((EstadoCotizacion.Aceptada, "Rosa Quispe", "Conforme"), (aceptada.Estado, aceptada.FirmadaPor, aceptada.RespuestaCliente));
        Assert.Equal(ServicioCotizacionesPedido(q.Id), aceptada.PedidoId);
        var outbox = await _t.OutboxAsync();
        Assert.Single(outbox, e => e.EventType == SuiteEvents.QuoteAccepted);
        Assert.True(outbox.FindIndex(e => e.EventType == SuiteEvents.QuoteAccepted) < outbox.FindIndex(e => e.EventType == SuiteEvents.OrderCreated));
        var order = Assert.Single(outbox, e => e.EventType == SuiteEvents.OrderCreated).Payload!.Value;
        Assert.Single(order.GetProperty("lines").EnumerateArray());   // la opcional no pasa al pedido
        var notas = await _t.Crm.Actividades.InteraccionesAsync(_t.Customer);
        Assert.Contains(notas, i => i.Interaccion.Resumen == "Cotización COT-TEST-0001 aceptada en línea por Rosa Quispe");

        // El mismo evento otra vez, o el de otro equipo, no repite nada.
        await _t.Crm.SyncHandlers.HandlePublicLinkRespondedAsync(_t.Context.Hub.Responder(q.Id, aceptar: true));
        Assert.Single(await _t.OutboxAsync(), e => e.EventType == SuiteEvents.OrderCreated);
    }

    private static Guid ServicioCotizacionesPedido(Guid id) => VortexCrm.Core.Services.ServicioCotizaciones.PedidoIdPara(id);

    [Fact]
    public async Task El_rechazo_la_deja_rechazada_y_una_respuesta_a_una_version_reemplazada_queda_como_aviso()
    {
        var q = await CotizacionAsync();
        await _t.Crm.Cotizaciones.EnlaceParaAceptarAsync(q.Id);
        await _t.Crm.SyncHandlers.HandlePublicLinkRespondedAsync(_t.Context.Hub!.Responder(q.Id, aceptar: false, comentario: "Muy caro"));
        var rechazada = (await _t.Crm.Cotizaciones.ObtenerAsync(q.Id))!;
        Assert.Equal((EstadoCotizacion.Rechazada, "Muy caro"), (rechazada.Estado, rechazada.RespuestaCliente));

        // Nueva versión: retira el enlace anterior; si el cliente igual responde la vieja, no cambia y queda el aviso.
        var otra = await CotizacionAsync();
        await _t.Crm.Cotizaciones.EnlaceParaAceptarAsync(otra.Id);
        var v2 = await _t.Crm.Cotizaciones.NuevaVersionAsync(otra.Id);
        Assert.Equal(VortexPublicLinkStatus.Revoked, _t.Context.Hub.PorDocumento[otra.Id].Link.Status);
        Assert.Null((await _t.Crm.Cotizaciones.ObtenerAsync(v2))!.EnlaceUrl);
        await _t.Crm.SyncHandlers.HandlePublicLinkRespondedAsync(_t.Context.Hub.Responder(otra.Id, aceptar: true));
        Assert.Equal(EstadoCotizacion.Reemplazada, (await _t.Crm.Cotizaciones.ObtenerAsync(otra.Id))!.Estado);
        Assert.DoesNotContain(await _t.OutboxAsync(), e => e.EventType == SuiteEvents.OrderCreated);
        var notas = await _t.Crm.Actividades.InteraccionesAsync(_t.Customer);
        Assert.Contains(notas, i => i.Interaccion.Resumen.StartsWith("Rosa Quispe aceptó en línea la cotización COT-TEST-0002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sin_conexion_o_con_un_descuento_sin_aprobar_no_hay_enlace()
    {
        var q = await CotizacionAsync();
        _t.Context.Hub!.SinConexion = true;
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.EnlaceParaAceptarAsync(q.Id));
        Assert.Equal(EstadoCotizacion.Borrador, (await _t.Crm.Cotizaciones.ObtenerAsync(q.Id))!.Estado);

        _t.Context.Hub.SinConexion = false;
        await _t.Crm.Clientes.GuardarAjustesAsync(new Ajustes(ModoIgv.Incluido, null, null, DescuentoMaximo: 5));
        var conDescuento = (await _t.Crm.Cotizaciones.ObtenerAsync(q.Id))!;
        conDescuento.Lineas[0].PrecioUnitario = 15m;
        await _t.Crm.Cotizaciones.GuardarAsync(conDescuento);
        _t.Context.Role = CrmPermissions.Seller;
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.EnlaceParaAceptarAsync(q.Id));
        Assert.Empty(_t.Context.Hub.PorDocumento);
    }

    [Fact]
    public async Task La_aceptacion_aplicada_en_un_equipo_llega_a_los_demas()
    {
        var b = new TestWorkspace("EQPB", Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await b.InitializeAsync();
        try
        {
            var q = await CotizacionAsync();
            _t.Clock.Advance(TimeSpan.FromMinutes(1));
            await _t.Crm.Cotizaciones.EnlaceParaAceptarAsync(q.Id);
            _t.Clock.Advance(TimeSpan.FromMinutes(1));
            var respuesta = _t.Context.Hub!.Responder(q.Id, aceptar: true);
            await _t.Crm.SyncHandlers.HandlePublicLinkRespondedAsync(respuesta);
            foreach (var entry in (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
                await b.Crm.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(entry));

            // B ya la tiene aceptada (con el firmante) cuando le llega el evento del hub: no hace nada más.
            await b.Crm.SyncHandlers.HandlePublicLinkRespondedAsync(respuesta);
            var enB = (await b.Crm.Cotizaciones.ObtenerAsync(q.Id))!;
            Assert.Equal((EstadoCotizacion.Aceptada, "Rosa Quispe", ServicioCotizacionesPedido(q.Id)), (enB.Estado, enB.FirmadaPor, enB.PedidoId));
            Assert.Empty(await b.OutboxAsync());
        }
        finally
        {
            await b.DisposeAsync();
        }
    }
}
