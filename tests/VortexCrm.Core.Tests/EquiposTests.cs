using VortexApi.Client;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Equipos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 5b: equipos, territorios, visibilidad por jerarquía y comisiones.</summary>
public sealed class EquiposTests : IAsyncLifetime
{
    private static readonly Guid Luis = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Ana = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Beto = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Carla = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly Guid Arequipa = Guid.Parse("33333333-0000-0000-0000-0000000000a9");

    private readonly TestWorkspace _t = new(role: CrmPermissions.SalesManager);

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        var party = TestWorkspace.Party(Arequipa, "Textiles del Sur", "20999888777");
        await _t.Crm.Master.ApplyPartyAsync(party with
        {
            Data = party.Data with { Address = new VortexAddress("Calle Mercaderes 10", "Cercado", "Arequipa", "Arequipa", null, "PE") },
        });
        // Luis lidera Lima (con Ana); Beto está en Sur, un equipo dentro del de Luis; Carla, en otro equipo.
        var lima = new EquipoVenta { Nombre = "Lima", LiderId = Luis, LiderNombre = "Luis", Miembros = [new(Ana, "Ana")], Zonas = ["Lima"] };
        await _t.Crm.Equipos.GuardarEquipoAsync(lima);
        await _t.Crm.Equipos.GuardarEquipoAsync(new EquipoVenta
        {
            Nombre = "Sur", LiderId = Beto, LiderNombre = "Beto", Miembros = [new(Beto, "Beto")], Zonas = ["arequipa"], PadreId = lima.Id,
        });
        await _t.Crm.Equipos.GuardarEquipoAsync(new EquipoVenta { Nombre = "Norte", LiderId = Carla, LiderNombre = "Carla", Miembros = [new(Carla, "Carla")] });
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<Oportunidad> OportunidadAsync(string titulo, Guid responsable, string nombre, decimal monto, Guid? cliente = null)
    {
        var op = new Oportunidad { Titulo = titulo, ClienteId = cliente ?? _t.Customer, Monto = monto, ResponsableId = responsable, ResponsableNombre = nombre };
        await _t.Crm.Oportunidades.GuardarAsync(op);
        return op;
    }

    [Fact]
    public async Task Con_visibilidad_por_jerarquia_cada_uno_ve_lo_suyo_y_lo_de_sus_equipos()
    {
        await OportunidadAsync("De Ana", Ana, "Ana", 100);
        await OportunidadAsync("De Beto", Beto, "Beto", 100);
        await OportunidadAsync("De Carla", Carla, "Carla", 100);
        await OportunidadAsync("De Luis", Luis, "Luis", 100);

        async Task<string[]> VeAsync(Guid usuario)
        {
            _t.Context.UserId = usuario;
            return (await _t.Crm.Oportunidades.ListarAsync()).Select(o => o.Oportunidad.Titulo).Order().ToArray();
        }

        _t.Context.Role = CrmPermissions.Seller;
        Assert.Equal(4, (await VeAsync(Ana)).Length);   // por defecto todos ven todo
        _t.Context.Role = CrmPermissions.SalesManager;
        await _t.Crm.Equipos.GuardarVisibilidadAsync(Visibilidad.Jerarquia);
        _t.Context.Role = CrmPermissions.Seller;

        Assert.Equal(["De Ana"], await VeAsync(Ana));
        Assert.Equal(["De Ana", "De Beto", "De Luis"], await VeAsync(Luis));   // su equipo y el de abajo
        Assert.Equal(["De Beto"], await VeAsync(Beto));
        _t.Context.Role = CrmPermissions.SalesManager;
        Assert.Equal(4, (await VeAsync(Carla)).Length);   // el gerente ve todo

        // Un equipo no puede quedar dentro de sí mismo.
        var lima = (await _t.Crm.Equipos.EquiposAsync()).Single(e => e.Nombre == "Lima");
        var sur = (await _t.Crm.Equipos.EquiposAsync()).Single(e => e.Nombre == "Sur");
        lima.PadreId = sur.Id;
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Equipos.GuardarEquipoAsync(lima));
    }

    [Fact]
    public async Task Los_clientes_sin_responsable_se_reparten_por_territorio()
    {
        Assert.Equal(2, await _t.Crm.Cartera.AsignarPorTerritorioAsync());
        var lima = (await _t.Crm.Master.GetPartiesAsync(null, true, null, 1, 50)).Items.Single(p => p.Id == _t.Customer);
        var sur = (await _t.Crm.Master.GetPartiesAsync(null, true, null, 1, 50)).Items.Single(p => p.Id == Arequipa);
        Assert.Equal(("Ana", "Beto"), (lima.OwnerName, sur.OwnerName));
        Assert.Equal(0, await _t.Crm.Cartera.AsignarPorTerritorioAsync());

        _t.Context.Role = CrmPermissions.Seller;
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cartera.AsignarPorTerritorioAsync());
    }

    [Fact]
    public async Task Las_comisiones_pagan_la_tasa_hasta_la_meta_y_el_acelerador_en_lo_que_pasa()
    {
        var mes = new DateOnly(2026, 11, 1);
        await _t.Crm.Embudos.GuardarMetaAsync(mes, Ana, "Ana", 1000);
        foreach (var (titulo, quien, nombre, monto) in new[] { ("A1", Ana, "Ana", 800m), ("A2", Ana, "Ana", 700m), ("B1", Beto, "Beto", 500m) })
        {
            var op = await OportunidadAsync(titulo, quien, nombre, monto);
            await _t.Crm.Oportunidades.CambiarEtapaAsync(op.Id, EtapaOportunidad.Ganado);
        }
        await OportunidadAsync("Abierta", Ana, "Ana", 9000);
        await _t.Crm.Equipos.GuardarPlanAsync(new PlanComisiones(BasesComision.Ganado, 5, TasaSobreMeta: 8,
            TasaPorVendedor: new Dictionary<Guid, decimal> { [Beto] = 4 }));

        var comisiones = await _t.Crm.Equipos.ComisionesAsync(mes);
        var ana = comisiones.Single(c => c.VendedorId == Ana);
        Assert.Equal((1500m, 1000m, 150m, 90m, 2), (ana.Base, ana.Meta!.Value, ana.Avance!.Value, ana.Comision, ana.Operaciones));   // 1000×5 % + 500×8 %
        Assert.Equal(20m, comisiones.Single(c => c.VendedorId == Beto).Comision);   // 500 × 4 %

        // Un vendedor solo ve la suya; sobre lo cobrado cuenta su cartera.
        _t.Context.Role = CrmPermissions.Seller;
        _t.Context.UserId = Ana;
        Assert.Equal([Ana], (await _t.Crm.Equipos.ComisionesAsync(mes)).Select(c => c.VendedorId));
        _t.Context.Role = CrmPermissions.SalesManager;
        await _t.Crm.Cartera.AsignarAsync(_t.Customer, Ana, "Ana");
        await _t.Crm.Equipos.GuardarPlanAsync(new PlanComisiones(BasesComision.Cobrado, 3));
        var cobro = TestWorkspace.Event(SuiteEvents.PaymentReceived, new PaymentReceived(Guid.NewGuid(), _t.Customer, 2000, "PEN", "transfer", "C-1"), "ERP");
        await _t.Crm.SyncHandlers.HandlePaymentReceivedAsync(cobro with { OccurredAt = _t.Clock.GetUtcNow() });
        Assert.Equal(60m, (await _t.Crm.Equipos.ComisionesAsync(mes)).Single().Comision);
    }
}
