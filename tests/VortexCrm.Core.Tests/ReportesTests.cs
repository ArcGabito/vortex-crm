using System.Globalization;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Equipos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Pdf;
using VortexCrm.Core.Services;

namespace VortexCrm.Core.Tests;

/// <summary>Fase 5d: reporte comercial (embudo, conversión, ciclo, actividad y ranking) por rol, con CSV y PDF.</summary>
public sealed class ReportesTests : IAsyncLifetime
{
    private static readonly Guid Luis = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Ana = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Beto = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Carla = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly DateOnly Desde = new(2026, 11, 1), Hasta = new(2026, 11, 30);

    private readonly TestWorkspace _t = new(role: CrmPermissions.SalesManager);

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        var lima = new EquipoVenta { Nombre = "Lima", LiderId = Luis, LiderNombre = "Luis", Miembros = [new(Ana, "Ana")] };
        await _t.Crm.Equipos.GuardarEquipoAsync(lima);
        await _t.Crm.Equipos.GuardarEquipoAsync(new EquipoVenta { Nombre = "Sur", LiderId = Beto, LiderNombre = "Beto", Miembros = [new(Beto, "Beto")], PadreId = lima.Id });
        await _t.Crm.Equipos.GuardarEquipoAsync(new EquipoVenta { Nombre = "Norte", LiderId = Carla, LiderNombre = "Carla", Miembros = [new(Carla, "Carla")] });

        foreach (var (titulo, quien, nombre, monto, etapa) in new[]
        {
            ("A1", Ana, "Ana", 800m, EtapaOportunidad.Ganado), ("A2", Ana, "Ana", 700m, EtapaOportunidad.Ganado),
            ("B1", Beto, "Beto", 500m, EtapaOportunidad.Ganado), ("B2", Beto, "Beto", 300m, EtapaOportunidad.Perdido),
            ("C1", Carla, "Carla", 900m, EtapaOportunidad.Ganado), ("A3", Ana, "Ana", 9000m, EtapaOportunidad.Prospecto),
        })
        {
            var op = new Oportunidad { Titulo = titulo, ClienteId = _t.Customer, Monto = monto, ResponsableId = quien, ResponsableNombre = nombre };
            await _t.Crm.Oportunidades.GuardarAsync(op);
            if (etapa == EtapaOportunidad.Perdido)
                await _t.Crm.Oportunidades.PerderAsync(op.Id, "Precio", null);
            else if (etapa != EtapaOportunidad.Prospecto)
                await _t.Crm.Oportunidades.CambiarEtapaAsync(op.Id, etapa);
        }
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task El_gerente_ve_todo_con_embudo_tasa_de_cierre_y_ranking()
    {
        var r = await _t.Crm.Reportes.GenerarAsync(Desde, Hasta);
        Assert.Equal(("todo", 4, 2900m, 1, 80m), (r.Alcance, r.Ganadas, r.MontoGanado, r.Perdidas, r.TasaCierre!.Value));
        Assert.Equal((1, 9000m), (r.Embudo.Single(e => e.Etapa == EtapaOportunidad.Prospecto).Oportunidades, r.MontoAbierto));
        Assert.Equal(["Ana", "Carla", "Beto"], r.Vendedores.Select(v => v.Nombre));
        Assert.Equal(50m, r.Vendedores.Single(v => v.Nombre == "Beto").TasaCierre);
        Assert.Equal([("Precio", 1)], r.MotivosPerdida);

        var csv = ServicioReportes.Csv(r);
        Assert.Contains("Ana,2,1500", csv);
        var xlsx = ServicioReportes.Xlsx(r, "Vortex SAC");
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(xlsx)))
        {
            Assert.Equal(3, zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/")));
            var ranking = System.Xml.Linq.XDocument.Load(zip.GetEntry("xl/worksheets/sheet3.xml")!.Open());
            var values = ranking.Descendants().Where(e => e.Name.LocalName == "c").Select(e => e.Value).ToList();
            Assert.Contains("Ana", values);
            Assert.Contains("1500", values);   // monto ganado como número
        }
        var pdf = GeneradorPdfReportes.Generar(r, "Vortex SAC", CultureInfo.GetCultureInfo("es-PE"));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

        await Assert.ThrowsAsync<CrmValidationException>(() => _t.Crm.Reportes.GenerarAsync(Hasta, Desde));
    }

    [Fact]
    public async Task El_lider_ve_su_equipo_y_el_vendedor_solo_lo_suyo()
    {
        _t.Context.Role = CrmPermissions.Seller;
        _t.Context.UserId = Luis;
        var equipo = await _t.Crm.Reportes.GenerarAsync(Desde, Hasta);
        Assert.Equal(("equipo", 2000m), (equipo.Alcance, equipo.MontoGanado));
        Assert.Equal(["Ana", "Beto"], equipo.Vendedores.Select(v => v.Nombre));

        _t.Context.UserId = Ana;
        var propio = await _t.Crm.Reportes.GenerarAsync(Desde, Hasta);
        Assert.Equal(("propio", 2, 1500m, 9000m), (propio.Alcance, propio.Ganadas, propio.MontoGanado, propio.MontoAbierto));
        Assert.Equal(["Ana"], propio.Vendedores.Select(v => v.Nombre));
    }
}
