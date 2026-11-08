using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 2b de la hoja de ruta: leads con origen y conversión, y productos en la oportunidad.</summary>
public sealed class LeadsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();
    private static readonly Guid Hotel = Guid.Parse("33333333-0000-0000-0000-000000000009");
    private const string RucHotel = "20605555129";   // RUC válido (con dígito verificador)

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Crm.Master.ApplyPartyAsync(TestWorkspace.Party(Hotel, "Hotel Los Andes SAC", RucHotel));
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private static Lead NuevoLead(string? empresa = "Cafetería Aroma SAC", string? ruc = "20123456786") => new()
    {
        Nombre = "Carla Mendoza", Empresa = empresa, TipoDocumento = ruc is null ? null : "RUC", Documento = ruc,
        Telefono = "987654321", Email = "carla@aroma.pe", Origen = OrigenesLead.Referido, Notas = "Pidió precios por mayor",
    };

    [Fact]
    public async Task Un_lead_se_califica_y_al_convertirlo_crea_cliente_contacto_y_oportunidad()
    {
        var lead = NuevoLead();
        await _t.Crm.Leads.GuardarAsync(lead);
        await _t.Crm.Leads.CambiarEstadoAsync(lead.Id, EstadosLead.Contactado);
        await _t.Crm.Leads.CambiarEstadoAsync(lead.Id, EstadosLead.Calificado);
        Assert.Equal(1, (await _t.Crm.Leads.ContarAsync())[EstadosLead.Calificado]);

        var r = await _t.Crm.Leads.ConvertirAsync(lead.Id, new ConversionLead(TituloOportunidad: "Café para 3 locales", Monto: 2400));
        Assert.True(r.ClienteNuevo);

        var cliente = (await _t.Crm.Master.GetPartyOptionsAsync(suppliers: false)).Single(p => p.Id == r.ClienteId);
        Assert.Equal("Cafetería Aroma SAC", cliente.Label);
        var contacto = (await _t.Crm.Contactos.ObtenerAsync(r.ContactoId))!;
        Assert.Equal(("Carla", "Mendoza", r.ClienteId), (contacto.Nombres, contacto.Apellidos, contacto.ClienteId!.Value));
        var oportunidad = (await _t.Crm.Oportunidades.ObtenerAsync(r.OportunidadId!.Value))!;
        Assert.Equal(("Café para 3 locales", 2400m, r.ClienteId), (oportunidad.Titulo, oportunidad.Monto, oportunidad.ClienteId!.Value));
        Assert.Equal(r.ContactoId, oportunidad.ContactoId);

        lead = (await _t.Crm.Leads.ObtenerAsync(lead.Id))!;
        Assert.Equal(EstadosLead.Convertido, lead.Estado);
        Assert.Equal((r.ClienteId, r.ContactoId, r.OportunidadId), (lead.ClienteId!.Value, lead.ContactoId!.Value, lead.OportunidadId));

        // El cliente nuevo se da de alta en el hub; el lead, el contacto y la oportunidad se replican.
        var outbox = await _t.OutboxAsync();
        Assert.Contains(outbox, x => x.Kind == VortexOutboxKind.MasterUpsert && x.EntityId == r.ClienteId);
        Assert.Contains(outbox, x => x.EventType == SuiteEvents.RecordUpserted && x.SubjectType == CrmRecordKinds.Lead);

        var ex = await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Leads.ConvertirAsync(lead.Id, new ConversionLead()));
        Assert.Contains("ya se convirtió", ex.Message);
    }

    [Fact]
    public async Task Si_ya_hay_un_cliente_con_ese_documento_se_usa_y_no_se_duplica()
    {
        var lead = NuevoLead("Hotel Los Andes", RucHotel);
        await _t.Crm.Leads.GuardarAsync(lead);
        Assert.Equal(Hotel, (await _t.Crm.Leads.ClienteConDocumentoAsync(RucHotel))!.Id);

        var r = await _t.Crm.Leads.ConvertirAsync(lead.Id, new ConversionLead(CrearOportunidad: false));
        Assert.False(r.ClienteNuevo);
        Assert.Equal(Hotel, r.ClienteId);
        Assert.Null(r.OportunidadId);
        Assert.DoesNotContain(await _t.OutboxAsync(), x => x.Kind == VortexOutboxKind.MasterUpsert);
    }

    [Fact]
    public async Task Un_lead_incompleto_o_descartado_no_avanza()
    {
        var sinContacto = NuevoLead();
        sinContacto.Telefono = null;
        sinContacto.Email = null;
        var ex = await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Leads.GuardarAsync(sinContacto));
        Assert.Contains("teléfono o un correo", ex.Message);

        var rucMalo = NuevoLead(ruc: "20123456789");
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Leads.GuardarAsync(rucMalo));

        var lead = NuevoLead(empresa: null, ruc: null);
        await _t.Crm.Leads.GuardarAsync(lead);
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Leads.CambiarEstadoAsync(lead.Id, EstadosLead.Descartado));
        await _t.Crm.Leads.CambiarEstadoAsync(lead.Id, EstadosLead.Descartado, "Sin presupuesto");
        Assert.Equal("Sin presupuesto", (await _t.Crm.Leads.ObtenerAsync(lead.Id))!.MotivoDescarte);
        ex = await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Leads.ConvertirAsync(lead.Id, new ConversionLead()));
        Assert.Contains("descartado", ex.Message);

        // Reabierto, se convierte en una persona (sin empresa).
        await _t.Crm.Leads.CambiarEstadoAsync(lead.Id, EstadosLead.Nuevo);
        Assert.Null((await _t.Crm.Leads.ObtenerAsync(lead.Id))!.MotivoDescarte);
        var r = await _t.Crm.Leads.ConvertirAsync(lead.Id, new ConversionLead(CrearOportunidad: false));
        Assert.Equal("Carla Mendoza", (await _t.Crm.Master.GetPartyOptionsAsync(suppliers: false)).Single(p => p.Id == r.ClienteId).Label);
    }

    [Fact]
    public async Task Los_productos_de_la_oportunidad_dan_el_monto_viajan_y_van_en_deal_won()
    {
        var o = new Oportunidad
        {
            Titulo = "Café y pan para el hotel", ClienteId = Hotel, Monto = 1,
            Lineas = [new LineaOportunidad(_t.Coffee, "Café tostado 250 g", 10, 18.50m), new LineaOportunidad(_t.Bread, "Pan francés", 100, 0.50m)],
        };
        await _t.Crm.Oportunidades.GuardarAsync(o);
        var guardada = (await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!;
        Assert.Equal(235m, guardada.Monto);   // 185 + 50
        Assert.Equal(2, guardada.Lineas!.Count);

        // Cambiar de etapa no borra los productos.
        await _t.Crm.Oportunidades.CambiarEtapaAsync(o.Id, EtapaOportunidad.Ganado);
        guardada = (await _t.Crm.Oportunidades.ObtenerAsync(o.Id))!;
        Assert.Equal(2, guardada.Lineas!.Count);

        var won = (await _t.OutboxAsync()).Single(x => x.EventType == SuiteEvents.DealWon);
        var lines = won.Payload!.Value.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal(10m, lines[0].GetProperty("quantity").GetDecimal());

        // En otro equipo llegan con la oportunidad.
        var otro = new TestWorkspace("OTRO");
        await otro.InitializeAsync();
        try
        {
            foreach (var entry in (await _t.OutboxAsync()).Where(x => x.EventType == SuiteEvents.RecordUpserted))
                await otro.Crm.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(entry));
            var alla = (await otro.Crm.Oportunidades.ObtenerAsync(o.Id))!;
            Assert.Equal(235m, alla.Monto);
            Assert.Equal(["Café tostado 250 g", "Pan francés"], alla.Lineas!.Select(l => l.Descripcion));
        }
        finally
        {
            await otro.DisposeAsync();
        }
    }
}
