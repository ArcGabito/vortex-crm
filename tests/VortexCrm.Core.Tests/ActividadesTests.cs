using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Actividades;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 3 de la hoja de ruta: interacciones con el cliente, resultado de las tareas y plantillas de mensajes.</summary>
public sealed class ActividadesTests : IAsyncLifetime
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
    public async Task Una_llamada_registrada_se_ve_en_la_ficha_de_todos_los_equipos()
    {
        var llamada = _a.Crm.Actividades.Nueva(_a.Customer);
        llamada.Resumen = "Llamé para el pedido de enero";
        llamada.Resultado = "Pide cotización por 50 cajas";
        await _a.Crm.Actividades.RegistrarAsync(llamada);
        await Assert.ThrowsAsync<CrmValidationException>(() => _a.Crm.Actividades.RegistrarAsync(_a.Crm.Actividades.Nueva(_a.Customer)));

        await ReplicarAsync(_a, _b);
        var alla = Assert.Single(await _b.Crm.Actividades.InteraccionesAsync(_b.Customer));
        Assert.Equal((TipoInteraccion.Llamada, "Pide cotización por 50 cajas"), (alla.Interaccion.Tipo, alla.Interaccion.Resultado));

        // Un vendedor no corrige ni borra la de otro.
        _b.Context.Role = CrmPermissions.Seller;
        var ajena = alla.Interaccion.Clonar();
        ajena.Resultado = "Otro";
        await Assert.ThrowsAsync<CrmValidationException>(() => _b.Crm.Actividades.RegistrarAsync(ajena));
        await Assert.ThrowsAsync<CrmValidationException>(() => _b.Crm.Actividades.EliminarInteraccionAsync(ajena.Id));
    }

    [Fact]
    public async Task Completar_una_visita_con_su_resultado_la_deja_en_la_ficha()
    {
        var visita = _a.Crm.Tareas.Nueva(_a.Customer);
        visita.Titulo = "Visita a la bodega";
        visita.Tipo = TipoTarea.Visita;
        await _a.Crm.Tareas.GuardarAsync(visita);

        await _a.Crm.Tareas.CompletarAsync(visita.Id, "Le gustó la muestra; pide precio por volumen");
        var interaccion = Assert.Single(await _a.Crm.Actividades.InteraccionesAsync(_a.Customer)).Interaccion;
        Assert.Equal((TipoInteraccion.Visita, visita.Id, "Visita a la bodega"), (interaccion.Tipo, interaccion.TareaId, interaccion.Resumen));
        Assert.True((await _a.Crm.Tareas.ObtenerAsync(visita.Id))!.Completada);

        // Sin resultado solo se completa.
        var otra = _a.Crm.Tareas.Nueva(_a.Customer);
        otra.Titulo = "Llamar";
        await _a.Crm.Tareas.GuardarAsync(otra);
        await _a.Crm.Tareas.CompletarAsync(otra.Id);
        Assert.Single(await _a.Crm.Actividades.InteraccionesAsync(_a.Customer));

        // Sin cliente ni oportunidad no hay ficha: el resultado no deja una interacción suelta.
        var suelta = _a.Crm.Tareas.Nueva(null);
        suelta.Titulo = "Ordenar el catálogo";
        await _a.Crm.Tareas.GuardarAsync(suelta);
        await _a.Crm.Tareas.CompletarAsync(suelta.Id, "Listo");
        Assert.Single(await _a.Crm.Actividades.InteraccionesAsync());
    }

    [Fact]
    public async Task Las_plantillas_se_llenan_con_los_datos_del_cliente_y_solo_las_configura_el_gerente()
    {
        var plantilla = new Plantilla { Nombre = "Seguimiento", Cuerpo = "Hola {contacto}, soy {vendedor} de {empresa}. ¿Pudo revisar la cotización {cotizacion}? {desconocida}" };
        _a.Context.Role = CrmPermissions.Seller;
        await Assert.ThrowsAsync<CrmValidationException>(() => _a.Crm.Actividades.GuardarPlantillaAsync(plantilla));
        _a.Context.Role = CrmPermissions.SalesManager;
        await _a.Crm.Actividades.GuardarPlantillaAsync(plantilla);
        await ReplicarAsync(_a, _b);
        Assert.Single(await _b.Crm.Actividades.PlantillasAsync(CanalPlantilla.WhatsApp));

        var texto = Plantillas.Llenar(plantilla.Cuerpo, new DatosPlantilla(Contacto: "Rosa", Vendedor: "Luis", Empresa: "Vortex", Cotizacion: null));
        Assert.Equal("Hola Rosa, soy Luis de Vortex. ¿Pudo revisar la cotización ? {desconocida}", texto);

        var datos = await _a.Crm.Actividades.DatosAsync(_a.Customer);
        Assert.Equal("Luis", datos.Vendedor);
        Assert.NotNull(datos.Cliente);
    }
}
