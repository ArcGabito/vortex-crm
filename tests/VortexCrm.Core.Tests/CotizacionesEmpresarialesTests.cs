using VortexApi.Client;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Services;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 4a: listas de precios, tope de descuento con aprobación, versiones, líneas opcionales y plantillas.</summary>
public sealed class CotizacionesEmpresarialesTests : IAsyncLifetime
{
    // Café S/ 18.50 con IGV y pan S/ 0.50; el equipo es de la tienda (Store). Reloj: 2 de noviembre de 2026.
    private readonly TestWorkspace _t = new();
    private static readonly Guid Mayorista = Guid.Parse("44444444-0000-0000-0000-000000000001");

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private static VortexMasterChange<VortexPriceList> Lista(Guid id, string code, IReadOnlyList<Guid> sedes, params VortexPriceListLine[] lineas) =>
        new(1, id, VortexChangeOperation.Created, 1, DateTimeOffset.UtcNow, "HUB",
            new VortexPriceList(id, code, code, "PEN", null, null, sedes, true, lineas,
                new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    private async Task ClienteConListaAsync()
    {
        await _t.Crm.Master.ApplyPriceListAsync(Lista(Mayorista, "MAYORISTA", [],
            new VortexPriceListLine(_t.Coffee, 17m), new VortexPriceListLine(_t.Coffee, 15.5m, 12)));
        var party = TestWorkspace.Party(_t.Customer, "Bodega Sur", "20111222333");
        await _t.Crm.Master.ApplyPartyAsync(party with { Version = 2, Data = party.Data with { Credit = new VortexPartyCredit(PriceListId: Mayorista) } });
    }

    [Fact]
    public async Task El_precio_sale_de_la_lista_del_cliente_y_el_descuento_sobre_el_tope_necesita_aprobacion()
    {
        await ClienteConListaAsync();
        await _t.Crm.Clientes.GuardarAjustesAsync(new Ajustes(ModoIgv.Incluido, null, null, DescuentoMaximo: 5));

        var q = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer);
        var lista = await _t.Crm.Cotizaciones.ListaPreciosAsync(q.ClienteId, q.SedeId, q.Moneda);
        Assert.Equal("MAYORISTA", lista.Nombre);
        var cafe = (await _t.Crm.Master.GetItemAsync(_t.Coffee))!;
        q.AgregarLinea(cafe.CrearLinea(q, 2, lista));
        Assert.Equal(17m, q.Lineas.Single().PrecioUnitario);

        // Desde 12 rige la escala; el precio puesto a mano se conserva y su descuento se mide contra la lista.
        q.Lineas[0].Cantidad = 12;
        await _t.Crm.Cotizaciones.RepreciarAsync(q);
        Assert.Equal(15.5m, q.Lineas[0].PrecioUnitario);
        q.Lineas[0].PrecioUnitario = 14m;
        await _t.Crm.Cotizaciones.RepreciarAsync(q);
        Assert.Equal((14m, 15.5m), (q.Lineas[0].PrecioUnitario, q.Lineas[0].PrecioLista!.Value));
        Assert.Equal(9.68m, q.DescuentoMaximo);

        // El vendedor guarda, pero no la envía (ni por WhatsApp) sin la aprobación del gerente.
        _t.Context.Role = CrmPermissions.Seller;
        await _t.Crm.Cotizaciones.GuardarAsync(q);
        Assert.True((await _t.Crm.Cotizaciones.ListarAsync()).Single().PorAprobar);
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.CambiarEstadoAsync(q.Id, EstadoCotizacion.Enviada));
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.WhatsAppAsync(q.Id));
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.AprobarDescuentoAsync(q.Id));

        _t.Context.Role = CrmPermissions.SalesManager;
        await _t.Crm.Cotizaciones.AprobarDescuentoAsync(q.Id);

        _t.Context.Role = CrmPermissions.Seller;
        await _t.Crm.Cotizaciones.CambiarEstadoAsync(q.Id, EstadoCotizacion.Enviada);
        var enviada = (await _t.Crm.Cotizaciones.ObtenerAsync(q.Id))!;
        Assert.Equal((EstadoCotizacion.Enviada, "Luis", 9.68m), (enviada.Estado, enviada.AprobadaPor, enviada.DescuentoAprobado!.Value));
        Assert.False((await _t.Crm.Cotizaciones.ListarAsync()).Single().PorAprobar);

        // Bajar más el precio después de aprobado vuelve a pedir aprobación.
        enviada.Lineas[0].PrecioUnitario = 13m;
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.GuardarAsync(enviada));
    }

    [Fact]
    public async Task Las_opcionales_no_suman_ni_pasan_al_pedido_y_la_nueva_version_reemplaza_a_la_anterior()
    {
        var q = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer);
        var cafe = (await _t.Crm.Master.GetItemAsync(_t.Coffee))!;
        var pan = (await _t.Crm.Master.GetItemAsync(_t.Bread))!;
        q.AgregarLinea(cafe.CrearLinea(q, 10));
        var opcional = pan.CrearLinea(q, 4);
        opcional.Opcional = true;
        q.AgregarLinea(opcional);
        Assert.Equal((185m, 2m), (q.Totales.Total, q.TotalOpcionales));
        await _t.Crm.Cotizaciones.GuardarAsync(q);
        Assert.Equal(185m, (await _t.Crm.Cotizaciones.ListarAsync()).Single().Total);
        await _t.Crm.Cotizaciones.CambiarEstadoAsync(q.Id, EstadoCotizacion.Enviada);

        var v2 = await _t.Crm.Cotizaciones.NuevaVersionAsync(q.Id);
        var anterior = (await _t.Crm.Cotizaciones.ObtenerAsync(q.Id))!;
        var nueva = (await _t.Crm.Cotizaciones.ObtenerAsync(v2))!;
        Assert.Equal(EstadoCotizacion.Reemplazada, anterior.Estado);
        Assert.Equal(("COT-TEST-0001 v2", EstadoCotizacion.Borrador, q.Id), (nueva.Codigo, nueva.Estado, nueva.VersionDe!.Value));
        Assert.Equal([false, true], nueva.Lineas.Select(l => l.Opcional));
        Assert.Equal(2, (await _t.Crm.Cotizaciones.VersionesAsync(nueva)).Count);
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.CambiarEstadoAsync(q.Id, EstadoCotizacion.Aceptada));

        await _t.Crm.Cotizaciones.CambiarEstadoAsync(v2, EstadoCotizacion.Aceptada);
        var numero = await _t.Crm.Cotizaciones.GenerarPedidoAsync(v2);
        Assert.Equal("PED-TEST-0001-V2", numero);
        var outbox = await _t.OutboxAsync();
        var aceptada = outbox.Single(e => e.EventType == SuiteEvents.QuoteAccepted).Payload!.Value;
        Assert.Equal(_t.Coffee, Assert.Single(aceptada.GetProperty("lines").EnumerateArray()).GetProperty("itemId").GetGuid());
        Assert.Equal(185m, aceptada.GetProperty("total").GetDecimal());
        var pedido = outbox.Single(e => e.EventType == SuiteEvents.OrderCreated).Payload!.Value;
        Assert.Single(pedido.GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task La_plantilla_se_comparte_y_llena_la_cotizacion_con_el_precio_de_lista_de_hoy()
    {
        var q = await _t.Crm.Cotizaciones.NuevaAsync();
        var cafe = (await _t.Crm.Master.GetItemAsync(_t.Coffee))!;
        q.AgregarLinea(cafe.CrearLinea(q, 12));
        q.AgregarLinea(new LineaCotizacion { Descripcion = "Instalación", Unidad = "SERV", Cantidad = 1, PrecioUnitario = 50, Opcional = true });
        q.Condiciones = "Entrega en 48 h";
        q.ValidaHasta = q.FechaEmision.AddDays(30);
        var id = await _t.Crm.Cotizaciones.GuardarPlantillaAsync("Kit café oficina", q);

        var record = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.RecordUpserted).Payload!.Value;
        Assert.Equal(CrmRecordKinds.PlantillaCotizacion, record.GetProperty("kind").GetString());
        var plantilla = Assert.Single(await _t.Crm.Cotizaciones.PlantillasAsync());
        Assert.Equal(("Kit café oficina", 2), (plantilla.Nombre, plantilla.Productos.Count));

        // Para un cliente con lista mayorista, el café entra a su precio de 12 unidades.
        await ClienteConListaAsync();
        var nueva = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer);
        await _t.Crm.Cotizaciones.AplicarPlantillaAsync(nueva, id);
        Assert.Equal([15.5m, 50m], nueva.Lineas.Select(l => l.PrecioUnitario));
        Assert.Equal([false, true], nueva.Lineas.Select(l => l.Opcional));
        Assert.Equal(("Entrega en 48 h", nueva.FechaEmision.AddDays(30)), (nueva.Condiciones, nueva.ValidaHasta));
        Assert.Equal(186m, nueva.Totales.Total);

        _t.Context.Role = CrmPermissions.Seller;
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.EliminarPlantillaAsync(id));
    }
}
