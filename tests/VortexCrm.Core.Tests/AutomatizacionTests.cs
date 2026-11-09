using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Actividades;
using VortexCrm.Core.Domain.Automatizacion;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 5a: reglas, oportunidades estancadas, secuencias y asignación automática de leads.</summary>
public sealed class AutomatizacionTests : IAsyncLifetime
{
    private static readonly Guid Ana = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Beto = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    // Dos equipos del mismo vendedor (Luis) y uno de otro vendedor.
    private readonly TestWorkspace _a = new("EQPA", role: CrmPermissions.Seller);
    private readonly TestWorkspace _b = new("EQPB", role: CrmPermissions.Seller);
    private readonly TestWorkspace _otro = new("EQPC", Ana, role: CrmPermissions.Seller);

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
        await _otro.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        await _otro.DisposeAsync();
    }

    private static async Task ReplicarAsync(TestWorkspace origen, params TestWorkspace[] destinos)
    {
        foreach (var entry in await origen.OutboxAsync())
        {
            if (entry.EventType == SuiteEvents.RecordUpserted)
                foreach (var d in destinos)
                    await d.Crm.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(entry));
            await origen.Crm.SyncStore.RemoveAsync(entry.Id);
        }
    }

    private static void Avanzar(TimeSpan t, params TestWorkspace[] ws)
    {
        foreach (var w in ws)
            w.Clock.Advance(t);
    }

    private static async Task<List<Tarea>> TareasAsync(TestWorkspace w) =>
        (await w.Crm.Tareas.ListarAsync()).Select(t => t.Tarea).ToList();

    [Fact]
    public async Task Una_oportunidad_quieta_15_dias_crea_una_sola_tarea_a_su_responsable_aunque_haya_varios_equipos()
    {
        var op = new Oportunidad { Titulo = "Uniformes 2027", ClienteId = _a.Customer, Monto = 3000 };
        await _a.Crm.Oportunidades.GuardarAsync(op);
        await ReplicarAsync(_a, _b, _otro);

        Avanzar(TimeSpan.FromDays(14), _a, _b, _otro);
        Assert.Equal(0, (await _a.Crm.Automatizacion.EjecutarAsync()).Total);

        Avanzar(TimeSpan.FromDays(2), _a, _b, _otro);
        // Los dos equipos de Luis la ven quieta a la vez, sin conexión: crean la misma tarea (mismo Id).
        Assert.Equal(1, (await _a.Crm.Automatizacion.EjecutarAsync()).Tareas);
        Assert.Equal(1, (await _b.Crm.Automatizacion.EjecutarAsync()).Tareas);
        Assert.Equal(0, (await _otro.Crm.Automatizacion.EjecutarAsync()).Total);   // no es suya
        var enA = Assert.Single(await TareasAsync(_a));
        Assert.Equal(Assert.Single(await TareasAsync(_b)).Id, enA.Id);
        Assert.Equal(("Retomar Uniformes 2027 (sin movimiento)", op.Id, true), (enA.Titulo, enA.OportunidadId!.Value, enA.EsAutomatica));

        // Al sincronizar quedan en una sola, y ya no se repite aunque el vendedor la borre.
        await ReplicarAsync(_a, _b, _otro);
        await ReplicarAsync(_b, _a, _otro);
        Assert.Single(await TareasAsync(_otro));
        await _a.Crm.Tareas.EliminarAsync(enA.Id);
        Avanzar(TimeSpan.FromDays(1), _a, _b);
        Assert.Equal(0, (await _a.Crm.Automatizacion.EjecutarAsync()).Total);
        Assert.Equal(0, (await _b.Crm.Automatizacion.EjecutarAsync()).Total);

        // Se mueve (una llamada registrada) y vuelve a quedarse quieta 15 días: avisa otra vez.
        await _a.Crm.Actividades.RegistrarAsync(new Interaccion
        {
            ClienteId = _a.Customer, OportunidadId = op.Id, Tipo = TipoInteraccion.Llamada, Fecha = _a.Clock.GetUtcNow(), Resumen = "Sigue evaluando",
        });
        Avanzar(TimeSpan.FromDays(10), _a);
        Assert.Equal(0, (await _a.Crm.Automatizacion.EjecutarAsync()).Total);
        Avanzar(TimeSpan.FromDays(6), _a);
        Assert.Equal(1, (await _a.Crm.Automatizacion.EjecutarAsync()).Tareas);
    }

    [Fact]
    public async Task Las_reglas_de_cotizaciones_y_leads_crean_tareas_y_solo_el_gerente_las_configura()
    {
        await Assert.ThrowsAsync<CrmValidationException>(() => _a.Crm.Automatizacion.GuardarReglaAsync(new Regla { Nombre = "x" }));
        _a.Context.Role = CrmPermissions.SalesManager;
        await _a.Crm.Automatizacion.GuardarReglaAsync(new Regla
        {
            Nombre = "Seguimiento de cotización", Disparador = DisparadorRegla.CotizacionSinRespuesta, Dias = 3,
            TituloTarea = "Llamar por {registro}", TipoTarea = TipoTarea.Llamada, DiasParaVencer = 1,
        });
        await _a.Crm.Automatizacion.GuardarReglaAsync(new Regla { Nombre = "Lead olvidado", Disparador = DisparadorRegla.LeadSinAtender, Dias = 1 });
        // Guardar otra regla deja guardada la predeterminada, y se puede apagar.
        var reglas = await _a.Crm.Automatizacion.ReglasAsync();
        Assert.Equal(3, reglas.Count);
        var estancada = reglas.Single(r => r.Id == Regla.EstancadaPredeterminada);
        estancada.Activa = false;
        await _a.Crm.Automatizacion.GuardarReglaAsync(estancada);
        _a.Context.Role = CrmPermissions.Seller;

        var q = await _a.Crm.Cotizaciones.NuevaAsync(_a.Customer);
        q.AgregarLinea(new LineaCotizacion { Descripcion = "Polo", Cantidad = 10, PrecioUnitario = 30 });
        await _a.Crm.Cotizaciones.GuardarAsync(q);
        await _a.Crm.Cotizaciones.CambiarEstadoAsync(q.Id, EstadoCotizacion.Enviada);
        await _a.Crm.Leads.GuardarAsync(new Lead { Nombre = "Carla Ruiz", Empresa = "Textiles SAC", Telefono = "987654321" });

        Avanzar(TimeSpan.FromDays(4), _a);
        var resultado = await _a.Crm.Automatizacion.EjecutarAsync();
        Assert.Equal(2, resultado.Tareas);
        var tareas = await TareasAsync(_a);
        var cotizacion = tareas.Single(t => t.Titulo.StartsWith("Llamar por", StringComparison.Ordinal));
        Assert.Equal(("Llamar por COT-EQPA-0001 · Bodega Don Pepe", new DateOnly(2026, 11, 7)), (cotizacion.Titulo, cotizacion.Fecha));
        Assert.Contains("987654321", tareas.Single(t => t.Titulo == "Lead olvidado").Notas);
        Assert.Equal(0, (await _a.Crm.Automatizacion.EjecutarAsync()).Total);
    }

    [Fact]
    public async Task La_secuencia_crea_sus_pasos_y_se_detiene_al_cerrar_la_oportunidad()
    {
        _a.Context.Role = CrmPermissions.SalesManager;
        var secuencia = new Secuencia
        {
            Nombre = "Cierre en 7 días",
            Pasos = [new(0, TipoTarea.Llamada, "Llamar a {registro}"), new(3, TipoTarea.WhatsApp, "WhatsApp de seguimiento"), new(7, TipoTarea.Visita, "Visita")],
        };
        await _a.Crm.Automatizacion.GuardarSecuenciaAsync(secuencia);
        _a.Context.Role = CrmPermissions.Seller;

        var op = new Oportunidad { Titulo = "Café para oficinas", ClienteId = _a.Customer, Monto = 900 };
        await _a.Crm.Oportunidades.GuardarAsync(op);
        Assert.Equal(3, await _a.Crm.Automatizacion.IniciarSecuenciaAsync(secuencia.Id, oportunidadId: op.Id));
        var tareas = (await TareasAsync(_a)).OrderBy(t => t.Fecha).ToList();
        Assert.Equal(["Llamar a Café para oficinas", "WhatsApp de seguimiento", "Visita"], tareas.Select(t => t.Titulo));
        Assert.Equal([new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 9)], tareas.Select(t => t.Fecha));

        await _a.Crm.Tareas.CompletarAsync(tareas[0].Id);
        await _a.Crm.Oportunidades.CambiarEtapaAsync(op.Id, EtapaOportunidad.Ganado);
        Assert.Equal(2, (await _a.Crm.Automatizacion.EjecutarAsync()).Detenidas);
        Assert.All(await TareasAsync(_a), t => Assert.True(t.Completada));

        // La regla que inicia una secuencia la necesita, y una secuencia en uso no se elimina.
        _a.Context.Role = CrmPermissions.SalesManager;
        await Assert.ThrowsAsync<CrmValidationException>(() => _a.Crm.Automatizacion.GuardarReglaAsync(new Regla
        {
            Nombre = "Sin secuencia", Disparador = DisparadorRegla.LeadSinAtender, Accion = AccionRegla.IniciarSecuencia,
        }));
        await _a.Crm.Automatizacion.GuardarReglaAsync(new Regla
        {
            Nombre = "Lead a secuencia", Disparador = DisparadorRegla.LeadSinAtender, Dias = 0, Accion = AccionRegla.IniciarSecuencia,
            SecuenciaId = secuencia.Id,
        });
        await Assert.ThrowsAsync<CrmValidationException>(() => _a.Crm.Automatizacion.EliminarSecuenciaAsync(secuencia.Id));
        await _a.Crm.Leads.GuardarAsync(new Lead { Nombre = "Mario", Telefono = "999888777" });
        Assert.Equal(1, (await _a.Crm.Automatizacion.EjecutarAsync()).Secuencias);
    }

    [Fact]
    public async Task Los_leads_nuevos_se_reparten_por_turno_o_por_carga()
    {
        _a.Context.Role = CrmPermissions.SalesManager;
        var vendedores = new List<Vendedor> { new(Ana, "Ana"), new(Beto, "Beto") };
        await _a.Crm.Automatizacion.GuardarAsignacionAsync(new AsignacionLeads(ModoAsignacion.Turno, vendedores));

        var nombres = new List<string?>();
        foreach (var n in new[] { "L1", "L2", "L3" })
        {
            var lead = new Lead { Nombre = n, Telefono = "999888777" };
            await _a.Crm.Leads.GuardarAsync(lead);
            nombres.Add((await _a.Crm.Leads.ObtenerAsync(lead.Id))!.ResponsableNombre);
            Avanzar(TimeSpan.FromMinutes(1), _a);
        }
        Assert.Equal(["Ana", "Beto", "Ana"], nombres);

        // Por carga: Beto tiene menos leads abiertos.
        await _a.Crm.Automatizacion.GuardarAsignacionAsync(new AsignacionLeads(ModoAsignacion.Carga, vendedores));
        var otro = new Lead { Nombre = "L4", Telefono = "999888777" };
        await _a.Crm.Leads.GuardarAsync(otro);
        Assert.Equal("Beto", (await _a.Crm.Leads.ObtenerAsync(otro.Id))!.ResponsableNombre);

        // Con responsable elegido a mano, o sin asignación, no se reparte.
        await _a.Crm.Automatizacion.GuardarAsignacionAsync(AsignacionLeads.Ninguna);
        var propio = new Lead { Nombre = "L5", Telefono = "999888777" };
        await _a.Crm.Leads.GuardarAsync(propio);
        Assert.Equal("Luis", (await _a.Crm.Leads.ObtenerAsync(propio.Id))!.ResponsableNombre);
    }
}
