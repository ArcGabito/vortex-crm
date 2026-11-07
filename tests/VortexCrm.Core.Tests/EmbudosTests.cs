using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 2 de la hoja de ruta: embudos configurables, motivos de pérdida, metas, pronóstico y sedes por usuario.</summary>
public sealed class EmbudosTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private static readonly Guid Calificacion = Guid.NewGuid(), Visita = Guid.NewGuid(), Propuesta = Guid.NewGuid(), Cierre = Guid.NewGuid();

    private static Embudo Proyectos() => new(Guid.NewGuid(), "Proyectos",
    [
        new(Calificacion, "Calificación", EtapaOportunidad.Prospecto, 10),
        new(Visita, "Visita técnica", EtapaOportunidad.Prospecto, 25, "#512BD4"),
        new(Propuesta, "Propuesta", EtapaOportunidad.Cotizado, 50),
        new(Cierre, "Negociación final", EtapaOportunidad.Negociacion, 80),
    ]);

    private async Task<Oportunidad> OportunidadAsync(string titulo, decimal monto, DateOnly? cierre = null)
    {
        var o = new Oportunidad { Titulo = titulo, ClienteId = _t.Customer, Monto = monto, FechaCierreEstimada = cierre };
        await _t.Crm.Oportunidades.GuardarAsync(o);
        return (await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!;
    }

    [Fact]
    public async Task Sin_embudos_configurados_se_usa_el_predeterminado()
    {
        var embudo = Assert.Single(await _t.Crm.Embudos.ListarAsync());
        Assert.True(embudo.EsPredeterminado);
        Assert.Equal([10, 40, 70], embudo.Etapas.Select(e => e.Probabilidad));

        var o = await OportunidadAsync("Café para la oficina", 1000);
        Assert.Null(o.EmbudoId);
        Assert.Equal(Embudo.EtapaPredeterminada(EtapaOportunidad.Prospecto), o.EtapaId);
    }

    [Fact]
    public async Task Una_oportunidad_recorre_las_etapas_de_su_embudo_y_las_cotizaciones_la_hacen_avanzar()
    {
        var proyectos = Proyectos();
        await _t.Crm.Embudos.GuardarAsync(proyectos);
        Assert.True((await _t.Crm.Embudos.ObtenerAsync(proyectos.Id)).PorDefecto);   // el primero queda por defecto

        var o = await OportunidadAsync("Equipamiento de cafetería", 5000);
        Assert.Equal(proyectos.Id, o.EmbudoId);
        Assert.Equal(Calificacion, o.EtapaId);

        await _t.Crm.Oportunidades.MoverAEtapaAsync(o.Id, Visita);
        o = (await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!;
        Assert.Equal((Visita, EtapaOportunidad.Prospecto), (o.EtapaId!.Value, o.Etapa));

        // Enviar una cotización la pasa a Cotizado: en este embudo, la etapa "Propuesta".
        var cotizacion = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer, o.Id);
        cotizacion.AgregarLinea((await _t.Crm.Master.GetItemAsync(_t.Coffee))!.CrearLinea(cotizacion, 10));
        await _t.Crm.Cotizaciones.GuardarAsync(cotizacion);
        await _t.Crm.Cotizaciones.CambiarEstadoAsync(cotizacion.Id, EstadoCotizacion.Enviada);
        o = (await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!;
        Assert.Equal((Propuesta, EtapaOportunidad.Cotizado), (o.EtapaId!.Value, o.Etapa));

        await _t.Crm.Oportunidades.CambiarEtapaAsync(o.Id, EtapaOportunidad.Ganado);
        Assert.Null((await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!.EtapaId);

        // Reabierta, vuelve a la etapa de negociación del embudo.
        await _t.Crm.Oportunidades.CambiarEtapaAsync(o.Id, EtapaOportunidad.Negociacion);
        Assert.Equal(Cierre, (await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!.EtapaId);
    }

    [Fact]
    public async Task Un_embudo_mal_armado_o_de_un_vendedor_se_rechaza()
    {
        var desordenado = Proyectos() with { Etapas = [.. Proyectos().Etapas.Reverse()] };
        var ex = await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Embudos.GuardarAsync(desordenado));
        Assert.Contains("Ordena las etapas", ex.Message);
        await Assert.ThrowsAsync<CrmValidationException>(() =>
            _t.Crm.Embudos.GuardarAsync(Proyectos() with { Etapas = [new(Guid.NewGuid(), "Ganada", EtapaOportunidad.Ganado, 100)] }));

        _t.Context.Role = CrmPermissions.Seller;
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Embudos.GuardarAsync(Proyectos()));
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Embudos.GuardarMetaAsync(new(2026, 11, 1), null, null, 1000));
    }

    [Fact]
    public async Task Perder_guarda_el_motivo_y_el_competidor()
    {
        Assert.Equal(6, (await _t.Crm.Clientes.ObtenerAjustesAsync()).Motivos.Count);
        await _t.Crm.Clientes.GuardarAjustesAsync(new Ajustes(ModoIgv.Incluido, null, ["Precio", "Plazo", " precio "]));
        Assert.Equal(["Precio", "Plazo", "precio"], (await _t.Crm.Clientes.ObtenerAjustesAsync()).Motivos);

        var o = await OportunidadAsync("Licitación", 8000);
        await _t.Crm.Oportunidades.PerderAsync(o.Id, "Precio", "Café del Norte SAC");
        o = (await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!;
        Assert.Equal(EtapaOportunidad.Perdido, o.Etapa);
        Assert.Equal(("Precio", "Café del Norte SAC"), (o.MotivoPerdida, o.Competidor));
    }

    [Fact]
    public async Task El_pronostico_pondera_por_probabilidad_y_compara_con_la_meta()
    {
        var noviembre = new DateOnly(2026, 11, 1);
        var a = await OportunidadAsync("A", 1000, new(2026, 11, 20));          // Prospecto 10 %: 100
        var b = await OportunidadAsync("B", 2000, new(2026, 10, 15));          // fecha pasada: cuenta en noviembre
        await _t.Crm.Oportunidades.CambiarEtapaAsync(b.Id, EtapaOportunidad.Negociacion);   // 70 %: 1400
        var c = await OportunidadAsync("C", 500, new(2026, 11, 30));
        await _t.Crm.Oportunidades.CambiarEtapaAsync(c.Id, EtapaOportunidad.Ganado);
        await OportunidadAsync("D", 3000, new(2026, 12, 10));                   // diciembre: 300
        await OportunidadAsync("E", 700);                                       // sin fecha
        await _t.Crm.Embudos.GuardarMetaAsync(noviembre, null, null, 5000);
        await _t.Crm.Embudos.GuardarMetaAsync(noviembre.AddDays(10), _t.Context.UserId, "Luis", 2000);
        await _t.Crm.Embudos.GuardarMetaAsync(noviembre, null, null, 4000);      // reemplaza la del equipo

        var p = await _t.Crm.Embudos.PronosticoAsync();
        var mes = p.Meses[0];
        Assert.Equal(noviembre, mes.Mes);
        Assert.Equal(500m, mes.Ganado);
        Assert.Equal(1500m, mes.Ponderado);
        Assert.Equal(2000m, mes.Esperado);
        Assert.Equal(4000m, mes.Meta);
        Assert.Equal(12.5m, mes.AvancePct);
        Assert.Equal(300m, p.Meses[1].Ponderado);
        Assert.Equal(700m, p.SinFecha);

        var luis = Assert.Single(p.Vendedores);
        Assert.Equal((500m, 1500m, 2000m), (luis.Ganado, luis.Ponderado, luis.Meta!.Value));
        Assert.Equal(2, (await _t.Crm.Embudos.MetasAsync(noviembre)).Count);
    }

    [Fact]
    public async Task Embudos_y_metas_llegan_a_los_otros_equipos()
    {
        await _t.Crm.Embudos.GuardarAsync(Proyectos());
        await _t.Crm.Embudos.GuardarMetaAsync(new(2026, 11, 1), null, null, 9000);
        var other = new TestWorkspace("OTRO");
        await other.InitializeAsync();
        try
        {
            foreach (var e in (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
                await other.Crm.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(e));
            Assert.Equal("Proyectos", Assert.Single(await other.Crm.Embudos.ListarAsync()).Nombre);
            Assert.Equal(9000m, Assert.Single(await other.Crm.Embudos.MetasAsync(new(2026, 11, 1))).Monto);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task Un_usuario_solo_cotiza_desde_sus_sedes()
    {
        _t.Context.SiteIds = [_t.Central];
        var cotizacion = await _t.Crm.Cotizaciones.NuevaAsync(_t.Customer, null);
        Assert.Equal(_t.Central, cotizacion.SedeId);   // la sede del equipo (Store) no es suya
        cotizacion.AgregarLinea((await _t.Crm.Master.GetItemAsync(_t.Coffee))!.CrearLinea(cotizacion, 1));
        cotizacion.SedeId = _t.Store;
        var ex = await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cotizaciones.GuardarAsync(cotizacion));
        Assert.Contains("No tienes asignada", ex.Message);
    }
}
