using VortexApi.Client;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Segmentos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Services;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 5c: etiquetas y segmentos guardados, campos personalizados y jerarquía de empresas.</summary>
public sealed class SegmentosTests : IAsyncLifetime
{
    private static readonly Guid Luis = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Ana = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Holding = Guid.Parse("33333333-0000-0000-0000-0000000000d1");
    private static readonly Guid SucursalNorte = Guid.Parse("33333333-0000-0000-0000-0000000000d2");
    private static readonly Guid SucursalSur = Guid.Parse("33333333-0000-0000-0000-0000000000d3");

    private readonly TestWorkspace _t = new(role: CrmPermissions.SalesManager);

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Crm.Master.ApplyPartyAsync(TestWorkspace.Party(Holding, "Grupo Andino SAA", "20500000001"));
        var norte = TestWorkspace.Party(SucursalNorte, "Andino Norte SAC", "20500000002");
        await _t.Crm.Master.ApplyPartyAsync(norte with
        {
            Data = norte.Data with { Address = new VortexAddress("Av. España 1", "Trujillo", "Trujillo", "La Libertad", null, "PE") },
        });
        await _t.Crm.Master.ApplyPartyAsync(TestWorkspace.Party(SucursalSur, "Andino Sur SAC", "20500000003"));
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private ServicioSegmentos Segmentos => _t.Crm.Segmentos;

    [Fact]
    public async Task Las_etiquetas_y_los_segmentos_guardados_filtran_la_cartera()
    {
        await Segmentos.GuardarEtiquetasAsync(_t.Customer, ["  Mayorista ", "VIP", "vip", "Horeca"]);
        await Segmentos.GuardarEtiquetasAsync(Holding, ["mayorista"]);
        await Segmentos.GuardarEtiquetasAsync(SucursalNorte, ["vip"]);
        Assert.Equal(["mayorista", "vip", "horeca"], (await Segmentos.PerfilAsync(_t.Customer)).Etiquetas);

        var usos = await Segmentos.EtiquetasAsync();
        Assert.Equal([("mayorista", 2), ("vip", 2), ("horeca", 1)], usos.Select(u => (u.Etiqueta, u.Clientes)));

        async Task<string[]> Nombres(FiltroClientes f) => (await Segmentos.BuscarAsync(f)).Items.Select(x => x.Cliente.Name).Order().ToArray();
        Assert.Equal(["Andino Norte SAC", "Bodega Don Pepe", "Grupo Andino SAA"], await Nombres(new FiltroClientes(Etiquetas: ["MAYORISTA", "vip"])));
        Assert.Equal(["Bodega Don Pepe"], await Nombres(new FiltroClientes(Etiquetas: ["mayorista", "vip"], TodasLasEtiquetas: true)));
        Assert.Equal(["Andino Norte SAC"], await Nombres(new FiltroClientes(Ciudad: "trujillo")));

        // Un segmento guardado privado solo lo ve quien lo creó; uno compartido, todos.
        var id = await Segmentos.GuardarListaAsync(new ListaGuardada(Guid.Empty, "VIP de Lima", new FiltroClientes(Etiquetas: ["vip"], Ciudad: "Lima"), false, null, null));
        await Segmentos.GuardarListaAsync(new ListaGuardada(Guid.Empty, "Mayoristas", new FiltroClientes(Etiquetas: ["mayorista"]), true, null, null));
        Assert.Equal(["Mayoristas", "VIP de Lima"], (await Segmentos.ListasAsync()).Select(l => l.Nombre));
        _t.Context.UserId = Ana;
        _t.Context.Role = CrmPermissions.Seller;
        Assert.Equal(["Mayoristas"], (await Segmentos.ListasAsync()).Select(l => l.Nombre));
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.EliminarListaAsync(id));

        _t.Context.UserId = Luis;
        _t.Context.Role = CrmPermissions.SalesManager;
        var lista = (await Segmentos.ListasAsync()).Single(l => l.Id == id);
        Assert.Equal(["Bodega Don Pepe"], await Nombres(lista.Filtro));

        var csv = await Segmentos.ExportarCsvAsync(lista.Filtro);
        Assert.Contains("Bodega Don Pepe", csv);
        Assert.Contains("mayorista; vip; horeca", csv);
    }

    [Fact]
    public async Task La_jerarquia_de_empresas_agrupa_las_sucursales_sin_ciclos()
    {
        await Segmentos.AsignarMatrizAsync(SucursalNorte, Holding);
        await Segmentos.AsignarMatrizAsync(SucursalSur, Holding);
        var grupo = await Segmentos.GrupoAsync(Holding);
        Assert.Null(grupo.Matriz);
        Assert.Equal(["Andino Norte SAC", "Andino Sur SAC"], grupo.Sucursales.Select(s => s.Name));
        Assert.Equal("Grupo Andino SAA", (await Segmentos.GrupoAsync(SucursalSur)).Matriz?.Name);

        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.AsignarMatrizAsync(Holding, SucursalNorte));   // ciclo
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.AsignarMatrizAsync(Holding, Holding));
        Assert.Equal(3, (await Segmentos.BuscarAsync(new FiltroClientes(GrupoDe: Holding))).Total);

        // Quitar la matriz conserva las etiquetas.
        await Segmentos.GuardarEtiquetasAsync(SucursalSur, ["sur"]);
        await Segmentos.AsignarMatrizAsync(SucursalSur, null);
        var perfil = await Segmentos.PerfilAsync(SucursalSur);
        Assert.Equal(["sur"], perfil.Etiquetas);
        Assert.Null(perfil.MatrizId);
        Assert.Single((await Segmentos.GrupoAsync(Holding)).Sucursales);
    }

    [Fact]
    public async Task Los_campos_personalizados_validan_su_tipo_y_sirven_para_segmentar()
    {
        var rubro = await Segmentos.GuardarCampoAsync(new CampoPersonalizado(Guid.Empty, EntidadesCampo.Cliente, "Rubro", TiposCampo.Lista,
            ["Restaurantes", "Hoteles"], Obligatorio: true));
        var aniversario = await Segmentos.GuardarCampoAsync(new CampoPersonalizado(Guid.Empty, EntidadesCampo.Cliente, "Aniversario", TiposCampo.Fecha));
        var licencia = await Segmentos.GuardarCampoAsync(new CampoPersonalizado(Guid.Empty, EntidadesCampo.Oportunidad, "Licencias", TiposCampo.Numero));
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.GuardarCampoAsync(
            new CampoPersonalizado(Guid.Empty, EntidadesCampo.Cliente, "rubro", TiposCampo.Texto)));
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.GuardarCampoAsync(
            new CampoPersonalizado(Guid.Empty, EntidadesCampo.Cliente, "Canal", TiposCampo.Lista)));

        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.GuardarValoresAsync(EntidadesCampo.Cliente, _t.Customer,
            new Dictionary<Guid, string?> { [aniversario] = "15/03/2010" }));                                           // falta el rubro
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.GuardarValoresAsync(EntidadesCampo.Cliente, _t.Customer,
            new Dictionary<Guid, string?> { [rubro] = "Bancos" }));
        await Segmentos.GuardarValoresAsync(EntidadesCampo.Cliente, _t.Customer,
            new Dictionary<Guid, string?> { [rubro] = "restaurantes", [aniversario] = "15/03/2010" });
        var valores = await Segmentos.ValoresAsync(_t.Customer);
        Assert.Equal(("Restaurantes", "2010-03-15"), (valores[rubro], valores[aniversario]));

        var op = new Oportunidad { Titulo = "Licencias", ClienteId = _t.Customer, Monto = 100, ResponsableId = Luis, ResponsableNombre = "Luis" };
        await _t.Crm.Oportunidades.GuardarAsync(op);
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.GuardarValoresAsync(EntidadesCampo.Oportunidad, op.Id,
            new Dictionary<Guid, string?> { [licencia] = "muchas" }));
        await Segmentos.GuardarValoresAsync(EntidadesCampo.Oportunidad, op.Id, new Dictionary<Guid, string?> { [licencia] = "1,5" });
        Assert.Equal("1.5", (await Segmentos.ValoresAsync(op.Id))[licencia]);

        var restaurantes = await Segmentos.BuscarAsync(new FiltroClientes(CampoId: rubro, CampoValor: "Restaurantes"));
        Assert.Equal([_t.Customer], restaurantes.Items.Select(x => x.Cliente.Id));
    }

    [Fact]
    public async Task Un_vendedor_no_etiqueta_clientes_de_otro_y_los_segmentos_viajan_a_otro_equipo()
    {
        await _t.Crm.Cartera.AsignarAsync(Holding, Ana, "Ana");
        _t.Context.Role = CrmPermissions.Seller;
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.GuardarEtiquetasAsync(Holding, ["vip"]));
        await Segmentos.GuardarEtiquetasAsync(_t.Customer, ["vip"]);
        await Assert.ThrowsAsync<CrmValidationException>(() => Segmentos.GuardarCampoAsync(
            new CampoPersonalizado(Guid.Empty, EntidadesCampo.Cliente, "Rubro", TiposCampo.Texto)));

        var otro = new TestWorkspace("OTRO");
        await otro.InitializeAsync();
        try
        {
            foreach (var e in (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
                await otro.Crm.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(e));
            Assert.Equal(["vip"], (await otro.Crm.Segmentos.PerfilAsync(_t.Customer)).Etiquetas);
        }
        finally
        {
            await otro.DisposeAsync();
        }
    }

    [Fact]
    public void Los_valores_se_normalizan_por_tipo()
    {
        var si = new CampoPersonalizado(Guid.NewGuid(), EntidadesCampo.Cliente, "Exporta", TiposCampo.SiNo);
        Assert.Equal(("true", (string?)null), si.Normalizar("Sí"));
        Assert.Equal(((string?)null, "si_no"), si.Normalizar("quizá"));
        Assert.Equal(((string?)null, (string?)null), si.Normalizar("  "));
        var numero = new CampoPersonalizado(Guid.NewGuid(), EntidadesCampo.Cliente, "Empleados", TiposCampo.Numero);
        Assert.Equal("1200.5", numero.Normalizar("1,200.5").Valor);
        Assert.Equal(["a b", "c"], Etiquetas.Normalizar(["A   B", " c ", "", "C"]));
    }
}
