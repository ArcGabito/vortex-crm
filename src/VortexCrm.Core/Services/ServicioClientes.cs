using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Services;

/// <summary>
/// La vista de 360° del cliente (ficha, actividad que informan los otros sistemas), el panel y los ajustes de ventas.
/// </summary>
public sealed class ServicioClientes(CrmDatabase db, ICrmContext context, TimeProvider clock,
    ServicioContactos contactos, ServicioOportunidades oportunidades, ServicioCotizaciones cotizaciones, ServicioTareas tareas)
    : CrmService(db, context, clock)
{
    public async Task<FichaCliente?> FichaAsync(Guid clienteId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var cliente = await c.FirstOrDefaultAsync("SELECT * FROM parties WHERE id = $id", MasterDataService.ReadParty, null, ("$id", clienteId));
        if (cliente is null)
            return null;
        return new FichaCliente(cliente,
            (await contactos.ListarAsync(null, clienteId, ct)).Select(v => v.Contacto).ToList(),
            await oportunidades.ListarAsync(null, clienteId, ct),
            await cotizaciones.ListarAsync(null, null, clienteId, null, ct),
            await tareas.ListarAsync(false, clienteId, null, ct),
            await ActividadAsync(clienteId, 30, ct));
    }

    public async Task<IReadOnlyList<ActividadRow>> ActividadAsync(Guid? clienteId, int limite = 20, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT * FROM actividad WHERE $cliente IS NULL OR cliente_id = $cliente ORDER BY ocurrido_en DESC LIMIT $limite
            """, r => new ActividadRow(r.Guid("id"), r.Guid("cliente_id"), r.Str("tipo"), r.Str("titulo"), r.StrOrNull("detalle"),
                r.DecOrNull("monto"), r.StrOrNull("moneda"), r.Str("origen"), r.Time("ocurrido_en")), null,
            ("$cliente", clienteId), ("$limite", limite));
    }

    public async Task<PanelCrm> PanelAsync(CancellationToken ct = default)
    {
        var hoy = FormatoPeru.Hoy(Clock);
        var todas = (await oportunidades.ListarAsync(ct: ct)).Select(v => v.Oportunidad).ToList();
        var ventas = VentasMensuales.Ultimos(todas, hoy);
        var agenda = AgendaTareas.Organizar((await tareas.ListarAsync(soloMias: true, ct: ct)).Select(t => t.Tarea), hoy);
        var abiertas = (await cotizaciones.ListarAsync(ct: ct)).Where(q => q.Estado is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada).ToList();
        return new PanelCrm(ResumenPipeline.Calcular(todas), ventas, ventas[^1].Monto, agenda.Hoy.Count, agenda.Vencidas.Count,
            abiertas.Count, abiertas.Sum(q => q.Total));
    }

    /// <summary>
    /// Tablero del panel: curva de lo ganado y lo cotizado de los últimos meses, indicadores con su variación y las
    /// oportunidades abiertas de mayor monto (con el nombre del cliente).
    /// </summary>
    public async Task<(TableroVentas Tablero, IReadOnlyList<OportunidadVista> Principales)> TableroAsync(int meses = 7,
        CancellationToken ct = default)
    {
        var hoy = FormatoPeru.Hoy(Clock);
        var vistas = await oportunidades.ListarAsync(ct: ct);
        var quotes = await cotizaciones.ListarAsync(ct: ct);
        var tablero = TableroVentas.Calcular(vistas.Select(v => v.Oportunidad), quotes.Select(q => (q.FechaEmision, q.Total, q.Estado)), hoy, meses);
        var porId = vistas.ToDictionary(v => v.Oportunidad.Id);
        return (tablero, tablero.Principales.Select(o => porId[o.Id]).ToList());
    }

    // ---------- Ajustes de ventas ----------

    public async Task<Ajustes> ObtenerAjustesAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await AjustesAsync(c);
    }

    public Task GuardarAjustesAsync(Ajustes ajustes, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.SettingsChanged, "settings", null, null,
                $"{ajustes.ModoIgvPredeterminado}; condiciones: {Clean(ajustes.CondicionesPredeterminadas) ?? "(ninguna)"}");
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Ajustes, Guid.Empty,
                new Registros.AjustesDatos(ajustes.ModoIgvPredeterminado.ToString(), Clean(ajustes.CondicionesPredeterminadas),
                    ajustes.MotivosPerdida?.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList()));
            return 0;
        }, ct);
    }

    internal static async Task<Ajustes> AjustesAsync(SqliteConnection c) =>
        await c.FirstOrDefaultAsync("SELECT * FROM ajustes WHERE id = 1",
            r => new Ajustes(Enum.Parse<ModoIgv>(r.Str("modo_igv")), r.StrOrNull("condiciones"),
                r.StrOrNull("motivos_perdida") is { } m ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(m) : null))
        ?? new Ajustes(ModoIgv.Incluido, null);

    // ---------- Actividad que informan los otros sistemas ----------

    /// <summary>Registra un hecho del cliente una sola vez (el Id es el del evento).</summary>
    internal static Task RegistrarActividadAsync(SqliteConnection c, SqliteTransaction tx, Guid eventoId, Guid clienteId, string tipo,
        string titulo, string? detalle, decimal? monto, string? moneda, string origen, DateTimeOffset ocurrido) =>
        c.ExecAsync("""
            INSERT OR IGNORE INTO actividad (id, cliente_id, tipo, titulo, detalle, monto, moneda, origen, ocurrido_en)
            VALUES ($id, $cliente, $tipo, $titulo, $detalle, $monto, $moneda, $origen, $at)
            """, tx, ("$id", eventoId), ("$cliente", clienteId), ("$tipo", tipo), ("$titulo", titulo), ("$detalle", detalle),
            ("$monto", monto), ("$moneda", moneda), ("$origen", origen), ("$at", ocurrido));
}
