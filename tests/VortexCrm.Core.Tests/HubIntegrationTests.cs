using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Sync;

namespace VortexCrm.Core.Tests;

/// <summary>Solo corre con un hub real: <c>VORTEX_HUB_URL=http://localhost:5299/ dotnet test</c> (organización demo).</summary>
public sealed class HubFactAttribute : FactAttribute
{
    public HubFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")))
            Skip = "Define VORTEX_HUB_URL para probar contra un hub real.";
    }
}

public sealed class HubIntegrationTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [HubFact]
    public async Task Cliente_y_cotizacion_aceptada_sin_conexion_llegan_al_hub()
    {
        var client = new VortexApiClient(
            new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")!) },
            new VortexApiClientOptions
            {
                ProductCode = CrmSyncHandlers.ProductCode,
                ProductKey = Environment.GetEnvironmentVariable("VORTEX_CRM_KEY") ?? "vxp_crm_dev_key_change_me",
            });
        var sessions = new VortexSessionManager(client, new InMemorySessionStore(), "vortex-crm-integration-tests");
        await sessions.LoginAsync("luis@vortex.local", "Suite-Local-2026!", "Pruebas Vortex CRM", "tests");
        var sync = _t.Crm.SyncHandlers.Register(new VortexSync(client, sessions, _t.Crm.SyncStore, CrmSyncHandlers.SyncOptions));

        var first = await sync.SyncAsync();
        Assert.True(first.Completed, first.InterruptedBy);

        // Todo se registra sin red: un cliente nuevo (dato del hub), una oportunidad y una cotización aceptada (datos del CRM).
        var tag = Random.Shared.Next(10_000_000, 99_999_999).ToString();
        var customer = await _t.Crm.Master.CreatePartyAsync(new PartyDraft($"Cliente de pruebas {tag}", "Person", true, false, "DNI", tag,
            null, "987654321", "Lima"));
        var oportunidad = new VortexCrm.Core.Domain.Ventas.Oportunidad { Titulo = "Prueba de integración", ClienteId = customer, Monto = 100 };
        await _t.Crm.Oportunidades.GuardarAsync(oportunidad);
        var cotizacion = await _t.Crm.Cotizaciones.NuevaAsync(customer, oportunidad.Id);
        cotizacion.AgregarLinea(new VortexCrm.Core.Domain.Ventas.LineaCotizacion { Descripcion = "Servicio", Cantidad = 1, PrecioUnitario = 100 });
        await _t.Crm.Cotizaciones.GuardarAsync(cotizacion);
        await _t.Crm.Cotizaciones.CambiarEstadoAsync(cotizacion.Id, VortexCrm.Core.Domain.Ventas.EstadoCotizacion.Aceptada);

        var second = await sync.SyncAsync();
        Assert.True(second.Completed, second.InterruptedBy);
        Assert.Equal(0, second.Failed);
        Assert.Equal(0, second.PendingOutbox);
        // crm.record.upserted: oportunidad, cotización (guardada y aceptada) y oportunidad ganada; + quote.accepted y deal.won.
        Assert.Equal(6, second.PushedEvents);
        Assert.Equal(1, second.PushedMasterWrites);   // cliente
    }
}
