using System.Text.Json;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Services;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 2c de la hoja de ruta: responsable del cliente, reasignación de cartera y duplicados.</summary>
public sealed class CarteraTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();
    private static readonly Guid Ana = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Otro = Guid.Parse("33333333-0000-0000-0000-000000000009");

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    /// <summary>El cliente como llega del hub, con condiciones del ERP que el CRM no debe tocar.</summary>
    private static VortexMasterChange<VortexParty> Party(Guid id, string name, string? ruc, string? email = null, string? phone = null,
        Guid? owner = null, string? ownerName = null) =>
        new(1, id, VortexChangeOperation.Created, 1, DateTimeOffset.UtcNow, "HUB",
            new VortexParty(id, true, false, "Company", name, null, ruc is null ? null : "RUC", ruc, email, phone,
                new VortexAddress("Av. Larco 123", "Miraflores", "Lima", null, null, "PE"), "Nota del ERP", true,
                new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), new VortexPartyCredit(30, 5000m, null),
                owner is null ? null : new VortexPartyAccount(owner, ownerName)));

    private async Task<JsonElement> UltimoUpsertAsync() =>
        (await _t.OutboxAsync()).Last(e => e.Kind == VortexOutboxKind.MasterUpsert).Payload!.Value;

    [Fact]
    public async Task El_responsable_llega_del_hub_y_al_asignarlo_se_devuelve_el_cliente_sin_tocar_el_credito()
    {
        await _t.Crm.Master.ApplyPartyAsync(Party(_t.Customer, "Bodega Don Pepe", "20123456789", owner: Ana, ownerName: "Ana"));
        Assert.Equal("Ana", (await _t.Crm.Master.GetPartyAsync(_t.Customer))!.OwnerName);
        Assert.Equal(["Ana", "Luis"], (await _t.Crm.Cartera.VendedoresAsync()).Select(v => v.Nombre));

        await _t.Crm.Cartera.AsignarAsync(_t.Customer, _t.Context.UserId, "Luis");
        var body = await UltimoUpsertAsync();
        Assert.Equal("Luis", body.GetProperty("account").GetProperty("ownerName").GetString());
        Assert.False(body.TryGetProperty("credit", out _));   // las condiciones del ERP quedan como estén
        Assert.Equal("Av. Larco 123", body.GetProperty("address").GetProperty("line").GetString());
        Assert.Equal("Nota del ERP", body.GetProperty("notes").GetString());
        Assert.Single((await _t.Crm.Master.GetPartiesAsync(null, true, null, 1, 10, owner: _t.Context.UserId)).Items);
    }

    [Fact]
    public async Task Un_vendedor_toma_un_cliente_libre_pero_no_el_de_otro()
    {
        await _t.Crm.Master.ApplyPartyAsync(Party(_t.Customer, "Bodega Don Pepe", "20123456789"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Otro, "Ferretería Lima", "20111222333", owner: Ana, ownerName: "Ana"));
        _t.Context.Role = CrmPermissions.Seller;

        await _t.Crm.Cartera.AsignarAsync(_t.Customer, _t.Context.UserId, "Luis");
        Assert.Equal(_t.Context.UserId, (await _t.Crm.Master.GetPartyAsync(_t.Customer))!.OwnerId);
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cartera.AsignarAsync(Otro, _t.Context.UserId, "Luis"));
        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Cartera.ReasignarCarteraAsync(Ana, _t.Context.UserId, "Luis"));
        await _t.Crm.Cartera.AsignarAsync(_t.Customer, null, null);   // soltar uno suyo
        Assert.Null((await _t.Crm.Master.GetPartyAsync(_t.Customer))!.OwnerId);
    }

    [Fact]
    public async Task Reasignar_la_cartera_mueve_los_clientes_y_sus_oportunidades_abiertas()
    {
        await _t.Crm.Master.ApplyPartyAsync(Party(_t.Customer, "Bodega Don Pepe", "20123456789", owner: Ana, ownerName: "Ana"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Otro, "Ferretería Lima", "20111222333", owner: Ana, ownerName: "Ana"));
        var abierta = new Oportunidad { Titulo = "Café 2027", ClienteId = _t.Customer, Monto = 900, ResponsableId = Ana, ResponsableNombre = "Ana" };
        await _t.Crm.Oportunidades.GuardarAsync(abierta);

        var movidos = await _t.Crm.Cartera.ReasignarCarteraAsync(Ana, _t.Context.UserId, "Luis");
        Assert.Equal(2, movidos);
        Assert.Equal(2, (await _t.Crm.Master.GetPartiesAsync(null, true, null, 1, 10, owner: _t.Context.UserId)).Total);
        Assert.Equal("Luis", (await _t.Crm.Oportunidades.ObtenerAsync(abierta.Id))!.ResponsableNombre);
        Assert.Equal(0, (await _t.Crm.Master.GetPartiesAsync(null, true, null, 1, 10, unassigned: true)).Total);
    }

    [Fact]
    public async Task Detecta_duplicados_por_documento_nombre_correo_y_telefono()
    {
        await _t.Crm.Master.ApplyPartyAsync(Party(_t.Customer, "Bodega Don Pepe S.A.C.", "20123456789"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Otro, "BODEGA DON PEPE", null));
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.NewGuid(), "Panadería Rosa", null, email: "rosa@pan.pe"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.NewGuid(), "Pan Rosita", null, email: " ROSA@pan.pe"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.NewGuid(), "Juan Pérez", null, phone: "+51 987 654 321"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.NewGuid(), "J. Perez", null, phone: "987654321"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.NewGuid(), "Otra empresa", "20999888777"));
        // Mismo teléfono pero dos RUC distintos: no son duplicados.
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.NewGuid(), "Ferretería Uno", "20100000001", phone: "011112222"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.NewGuid(), "Ferretería Dos", "20100000002", phone: "011112222"));

        var grupos = await _t.Crm.Cartera.DuplicadosAsync();
        Assert.Equal([MotivosDuplicado.Nombre, MotivosDuplicado.Correo, MotivosDuplicado.Telefono], grupos.Select(g => g.Motivo));
        Assert.All(grupos, g => Assert.Equal(2, g.Clientes.Count));
        Assert.Equal(_t.Customer, grupos[0].Clientes[0].Id);   // el que tiene documento va primero
        Assert.Equal("BODEGA DON PEPE", ServicioCartera.NombreNormalizado("Bodega Don Pepe S.A.C."));
        Assert.Equal("CAFE ARAMA", ServicioCartera.NombreNormalizado("Café Aramá E.I.R.L."));
    }

    [Fact]
    public async Task Fusionar_mueve_lo_del_duplicado_al_principal_y_lo_desactiva_en_el_hub()
    {
        await _t.Crm.Master.ApplyPartyAsync(Party(_t.Customer, "Bodega Don Pepe S.A.C.", "20123456789"));
        await _t.Crm.Master.ApplyPartyAsync(Party(Otro, "BODEGA DON PEPE", null, owner: Ana, ownerName: "Ana"));
        var contacto = new Contacto { Nombres = "Rosa", Apellidos = "Quispe", ClienteId = Otro };
        await _t.Crm.Contactos.GuardarAsync(contacto);
        var oportunidad = new Oportunidad { Titulo = "Uniformes", ClienteId = Otro, Monto = 3000 };
        await _t.Crm.Oportunidades.GuardarAsync(oportunidad);

        var resultado = await _t.Crm.Cartera.FusionarAsync(_t.Customer, Otro);
        Assert.Equal((1, 1), (resultado.Contactos, resultado.Oportunidades));
        var ficha = (await _t.Crm.Clientes.FichaAsync(_t.Customer))!;
        Assert.Single(ficha.Contactos);
        Assert.Single(ficha.Oportunidades);
        Assert.Equal("Ana", ficha.Cliente.OwnerName);   // el principal no tenía responsable: toma el del duplicado

        var upserts = (await _t.OutboxAsync()).Where(e => e.Kind == VortexOutboxKind.MasterUpsert).ToList();
        var duplicado = upserts.Single(e => e.EntityId == Otro).Payload!.Value;
        Assert.False(duplicado.GetProperty("isActive").GetBoolean());
        Assert.Contains("Fusionado con Bodega Don Pepe S.A.C.", duplicado.GetProperty("notes").GetString());
        Assert.Empty(await _t.Crm.Cartera.DuplicadosAsync());

        // Dos RUC distintos son dos empresas: no se fusionan.
        await _t.Crm.Master.ApplyPartyAsync(Party(Guid.Parse("33333333-0000-0000-0000-000000000010"), "Bodega Don Pepe", "20555666777"));
        await Assert.ThrowsAsync<CrmValidationException>(() =>
            _t.Crm.Cartera.FusionarAsync(_t.Customer, Guid.Parse("33333333-0000-0000-0000-000000000010")));
    }
}
