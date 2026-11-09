using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Automatizacion;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Automatización del CRM (fase 5a): reglas que crean tareas o inician secuencias (CRM-61, CRM-26), secuencias de
/// seguimiento (CRM-46) y asignación automática de leads (CRM-12).
/// <para>
/// <b>Varios equipos:</b> cada equipo ejecuta las reglas solo para los registros de su usuario (y el gerente, para los que
/// no tienen responsable). Cada disparo se guarda como registro replicado con Id determinista (regla + registro + la
/// situación), y la tarea que crea también: si dos equipos del mismo vendedor la crean a la vez, es la misma tarea.
/// </para>
/// </summary>
public sealed class ServicioAutomatizacion(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    /// <summary>Id fijo del registro de asignación de leads.</summary>
    public static readonly Guid AsignacionId = Guid.Parse("5e7a0c3d-0000-4000-8000-0000000a5160");

    /// <summary>Una oportunidad ganada hace más de esto no dispara el seguimiento de posventa de una regla recién creada.</summary>
    private const int VentanaGanadas = 30;

    // ---------- Reglas ----------

    /// <summary>Las reglas guardadas o, si no hay ninguna, la predeterminada (oportunidad quieta 15 días).</summary>
    public async Task<IReadOnlyList<Regla>> ReglasAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ReglasAsync(c, null);
    }

    private static async Task<IReadOnlyList<Regla>> ReglasAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        var reglas = await c.QueryAsync("SELECT datos FROM reglas", r => JsonSerializer.Deserialize<Regla>(r.Str("datos"), VortexJson.Options)!, tx);
        return reglas.Count == 0 ? [Regla.Predeterminada()] : reglas.OrderBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task GuardarReglaAsync(Regla regla, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        regla.Nombre = regla.Nombre.Trim();
        regla.TituloTarea = Clean(regla.TituloTarea);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var secuencias = (await SecuenciasAsync(c, tx)).Select(s => s.Id).ToList();
            var errores = regla.Validar(secuencias);
            Require(errores.Count == 0, string.Join(" ", errores));
            if (regla.Accion == AccionRegla.CrearTarea)
                regla.SecuenciaId = null;
            await GuardarPredeterminadaSiHaceFaltaAsync(c, tx, now, regla.Id);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Regla, regla.Id, regla);
            await AuditAsync(c, tx, now, "rule.saved", "rule", regla.Id, regla.Nombre, $"{regla.Disparador} {regla.Dias} → {regla.Accion}");
            return 0;
        }, ct);
    }

    /// <summary>Elimina la regla. La predeterminada, mientras no se guardó, queda desactivada (si no, volvería a aparecer).</summary>
    public Task EliminarReglaAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await GuardarPredeterminadaSiHaceFaltaAsync(c, tx, now, id);
            var restantes = await c.ScalarAsync<long>("SELECT COUNT(*) FROM reglas WHERE id <> $id", tx, ("$id", id));
            if (restantes == 0)
            {
                // Sin ninguna regla guardada volvería la predeterminada: se deja guardada y apagada.
                var apagada = Regla.Predeterminada();
                apagada.Activa = false;
                await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Regla, apagada.Id, apagada);
            }
            if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM reglas WHERE id = $id", tx, ("$id", id)) > 0 && id != Regla.EstancadaPredeterminada)
                await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Regla, id);
            await AuditAsync(c, tx, now, "rule.deleted", "rule", id, null, null);
            return 0;
        }, ct);
    }

    /// <summary>La primera vez que se guarda otra regla, la predeterminada (que no estaba guardada) se guarda también.</summary>
    private static async Task GuardarPredeterminadaSiHaceFaltaAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid otra)
    {
        if (otra == Regla.EstancadaPredeterminada || await c.ScalarAsync<long>("SELECT COUNT(*) FROM reglas", tx) > 0)
            return;
        var predeterminada = Regla.Predeterminada();
        await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Regla, predeterminada.Id, predeterminada);
    }

    // ---------- Secuencias ----------

    public async Task<IReadOnlyList<Secuencia>> SecuenciasAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SecuenciasAsync(c, null);
    }

    private static async Task<IReadOnlyList<Secuencia>> SecuenciasAsync(SqliteConnection c, SqliteTransaction? tx) =>
        (await c.QueryAsync("SELECT datos FROM secuencias", r => JsonSerializer.Deserialize<Secuencia>(r.Str("datos"), VortexJson.Options)!, tx))
        .OrderBy(s => s.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task GuardarSecuenciaAsync(Secuencia secuencia, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        secuencia.Nombre = secuencia.Nombre.Trim();
        secuencia.Pasos = secuencia.Pasos.Select(p => p with { Titulo = p.Titulo?.Trim() ?? "" }).OrderBy(p => p.Dia).ToList();
        var errores = secuencia.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Secuencia, secuencia.Id, secuencia);
            await AuditAsync(c, tx, now, "sequence.saved", "sequence", secuencia.Id, secuencia.Nombre, $"{secuencia.Pasos.Count} pasos");
            return 0;
        }, ct);
    }

    public Task EliminarSecuenciaAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var usada = (await ReglasAsync(c, tx)).FirstOrDefault(r => r.SecuenciaId == id && r.Accion == AccionRegla.IniciarSecuencia);
            Require(usada is null, T("error.secuencia_usada", usada?.Nombre));
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Secuencia, id);
            await AuditAsync(c, tx, now, "sequence.deleted", "sequence", id, null, null);
            return 0;
        }, ct);
    }

    /// <summary>
    /// Inicia la secuencia sobre una oportunidad, un lead o un cliente (uno de los tres): crea todas sus tareas, cada una para
    /// su día, a nombre del responsable del registro.
    /// </summary>
    public Task<int> IniciarSecuenciaAsync(Guid secuenciaId, Guid? oportunidadId = null, Guid? leadId = null, Guid? clienteId = null,
        CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var secuencia = (await SecuenciasAsync(c, tx)).FirstOrDefault(s => s.Id == secuenciaId);
            Require(secuencia is not null, T("error.la_secuencia_no_existe"));
            var registro = oportunidadId is { } o ? await OportunidadAsync(c, tx, o)
                : leadId is { } l ? await LeadAsync(c, tx, l)
                : clienteId is { } cl ? await ClienteAsync(c, tx, cl)
                : null;
            Require(registro is not null, T("error.secuencia_sin_registro"));
            RequireOwner(registro!.ResponsableId, registro.ResponsableNombre);
            var creadas = await CrearSecuenciaAsync(c, tx, now, secuencia!, registro with
            {
                ResponsableId = registro.ResponsableId ?? Context.UserId,
                ResponsableNombre = registro.ResponsableId is null ? Context.UserName : registro.ResponsableNombre,
            }, Guid.NewGuid());
            await AuditAsync(c, tx, now, "sequence.started", registro.Tipo, registro.Id, registro.Nombre, secuencia!.Nombre);
            return creadas;
        }, ct);
    }

    // ---------- Asignación de leads (CRM-12) ----------

    public async Task<AsignacionLeads> AsignacionAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await AsignacionAsync(c, null);
    }

    internal static async Task<AsignacionLeads> AsignacionAsync(SqliteConnection c, SqliteTransaction? tx) =>
        await c.FirstOrDefaultAsync("SELECT datos FROM configuracion WHERE id = $id",
            r => JsonSerializer.Deserialize<AsignacionDatos>(r.Str("datos"), VortexJson.Options)!.Asignacion, tx, ("$id", AsignacionId))
        ?? AsignacionLeads.Ninguna;

    public Task GuardarAsignacionAsync(AsignacionLeads asignacion, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var datos = new AsignacionDatos(AsignacionId, asignacion with { Vendedores = asignacion.Vendedores.DistinctBy(v => v.Id).ToList() });
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.AsignacionLeads, AsignacionId, datos);
            await AuditAsync(c, tx, now, "leads.assignment", "settings", null, asignacion.Modo.ToString(),
                string.Join(", ", asignacion.Vendedores.Select(v => v.Nombre)));
            return 0;
        }, ct);
    }

    public sealed record AsignacionDatos(Guid Id, AsignacionLeads Asignacion);

    /// <summary>
    /// A quién le toca un lead nuevo: por turno, el que sigue al último que recibió un lead de la lista; por carga, el de
    /// menos leads abiertos (empate: el que lleva más tiempo sin recibir). Null si no hay asignación automática.
    /// </summary>
    internal static async Task<Vendedor?> ElegirVendedorAsync(SqliteConnection c, SqliteTransaction tx)
    {
        var asignacion = await AsignacionAsync(c, tx);
        if (!asignacion.Activa)
            return null;
        var vendedores = asignacion.Vendedores;
        var ultimos = (await c.QueryAsync("""
                SELECT responsable_id, MAX(creado_en) AS ultimo,
                       SUM(CASE WHEN estado IN ('nuevo', 'contactado', 'calificado') THEN 1 ELSE 0 END) AS abiertos
                FROM leads WHERE responsable_id IS NOT NULL GROUP BY responsable_id
                """, r => (Id: r.Guid("responsable_id"), Ultimo: r.Str("ultimo"), Abiertos: r.Int("abiertos")), tx))
            .ToDictionary(x => x.Id);
        if (asignacion.Modo == ModoAsignacion.Turno)
        {
            var anterior = vendedores.Where(v => ultimos.ContainsKey(v.Id)).MaxBy(v => ultimos[v.Id].Ultimo);
            return anterior is null ? vendedores[0] : vendedores[(vendedores.ToList().IndexOf(anterior) + 1) % vendedores.Count];
        }
        return vendedores
            .OrderBy(v => ultimos.TryGetValue(v.Id, out var u) ? u.Abiertos : 0)
            .ThenBy(v => ultimos.TryGetValue(v.Id, out var u) ? u.Ultimo : "")
            .First();
    }

    // ---------- Motor ----------

    /// <summary>
    /// Ejecuta las reglas activas sobre los registros de este usuario (y, para el gerente, los que no tienen responsable), y
    /// da por hechas las tareas que faltan de las secuencias cuya oportunidad se cerró o cuyo lead ya no está abierto. La app
    /// lo llama después de sincronizar.
    /// </summary>
    public async Task<ResultadoAutomatizacion> EjecutarAsync(CancellationToken ct = default)
    {
        if (Context.UserId is null)
            return new(0, 0, 0);
        return await InTransactionAsync(async (c, tx, now) =>
        {
            var hoy = FormatoPeru.Hoy(Clock);
            var secuencias = (await SecuenciasAsync(c, tx)).ToDictionary(s => s.Id);
            int tareas = 0, iniciadas = 0;
            foreach (var regla in (await ReglasAsync(c, tx)).Where(r => r.Activa))
            {
                foreach (var (registro, situacion) in await CandidatosAsync(c, tx, regla, now, hoy))
                {
                    if (!MeToca(registro.ResponsableId))
                        continue;
                    var disparo = DeterministicId.From("regla|" + situacion, regla.Id, registro.Id);
                    if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM disparos WHERE id = $id", tx, ("$id", disparo)) > 0)
                        continue;
                    if (regla.Accion == AccionRegla.IniciarSecuencia)
                    {
                        if (regla.SecuenciaId is not { } s || !secuencias.TryGetValue(s, out var secuencia))
                            continue;
                        await CrearSecuenciaAsync(c, tx, now, secuencia, registro, disparo);
                        iniciadas++;
                    }
                    else
                    {
                        var tarea = new Tarea
                        {
                            Id = DeterministicId.From("regla_tarea", disparo), Titulo = regla.Titulo(registro.Nombre), Tipo = regla.TipoTarea,
                            Fecha = hoy.AddDays(regla.DiasParaVencer), ClienteId = registro.ClienteId, ContactoId = registro.ContactoId,
                            OportunidadId = registro.OportunidadId, ResponsableId = registro.ResponsableId,
                            ResponsableNombre = registro.ResponsableNombre, Notas = registro.Notas, Origen = $"regla:{regla.Id}",
                            CreadoEn = now,
                        };
                        await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Tarea, tarea.Id, Registros.Datos(tarea));
                        tareas++;
                    }
                    await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Disparo, disparo,
                        new Registros.DisparoDatos(disparo, regla.Id, registro.Id, now));
                    await AuditAsync(c, tx, now, "rule.fired", registro.Tipo, registro.Id, registro.Nombre, regla.Nombre);
                }
            }
            var detenidas = await DetenerSecuenciasAsync(c, tx, now);
            return new ResultadoAutomatizacion(tareas, iniciadas, detenidas);
        }, ct);
    }

    private bool MeToca(Guid? responsable) =>
        responsable == Context.UserId || (responsable is null && Context.Can(CrmPermissions.EditAny));

    /// <summary>Lo que cumple la condición de la regla, con la "situación" que la hace distinta de una anterior.</summary>
    private static async Task<List<(RegistroAuto Registro, string Situacion)>> CandidatosAsync(SqliteConnection c, SqliteTransaction tx,
        Regla regla, DateTimeOffset now, DateOnly hoy)
    {
        var limite = SqliteExtensions.Time(now.AddDays(-regla.Dias));
        string Dia(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        switch (regla.Disparador)
        {
            case DisparadorRegla.OportunidadEstancada:
                // Último movimiento: el cambio de la oportunidad, su última interacción o su última tarea hecha.
                return (await c.QueryAsync($"""
                    SELECT o.*, MAX(o.actualizado_en,
                                    COALESCE((SELECT MAX(i.fecha) FROM interacciones i WHERE i.oportunidad_id = o.id), ''),
                                    COALESCE((SELECT MAX(t.completada_en) FROM tareas t WHERE t.oportunidad_id = o.id), '')) AS movimiento
                    FROM oportunidades o WHERE o.etapa IN ('Prospecto', 'Cotizado', 'Negociacion')
                    """, r => (Registro: Oportunidad(r), Movimiento: r.Str("movimiento")), tx))
                    .Where(x => string.CompareOrdinal(x.Movimiento, limite) <= 0)
                    .Select(x => (x.Registro, "estancada:" + x.Movimiento)).ToList();

            case DisparadorRegla.OportunidadGanada:
                var desde = SqliteExtensions.Time(now.AddDays(-regla.Dias - VentanaGanadas));
                return (await c.QueryAsync("""
                    SELECT o.* FROM oportunidades o
                    WHERE o.etapa = 'Ganado' AND o.cerrado_en IS NOT NULL AND o.cerrado_en <= $limite AND o.cerrado_en >= $desde
                    """, r => (Oportunidad(r), "ganada"), tx, ("$limite", limite), ("$desde", desde))).ToList();

            case DisparadorRegla.LeadSinAtender:
                return (await c.QueryAsync("SELECT * FROM leads WHERE estado = 'nuevo' AND creado_en <= $limite",
                    r => (Lead(r), "lead"), tx, ("$limite", limite))).ToList();

            case DisparadorRegla.CotizacionSinRespuesta:
                return (await c.QueryAsync("""
                    SELECT q.*, p.name AS cliente FROM cotizaciones q LEFT JOIN parties p ON p.id = q.cliente_id
                    WHERE q.estado = 'Enviada' AND q.fecha_emision <= $dia
                    """, r => (Cotizacion(r), "sin_respuesta:" + r.Int("version")), tx, ("$dia", Dia(hoy.AddDays(-regla.Dias))))).ToList();

            case DisparadorRegla.CotizacionPorVencer:
                return (await c.QueryAsync("""
                    SELECT q.*, p.name AS cliente FROM cotizaciones q LEFT JOIN parties p ON p.id = q.cliente_id
                    WHERE q.estado = 'Enviada' AND q.valida_hasta >= $hoy AND q.valida_hasta <= $dia
                    """, r => (Cotizacion(r), "por_vencer:" + r.Str("valida_hasta")), tx, ("$hoy", Dia(hoy)),
                    ("$dia", Dia(hoy.AddDays(regla.Dias))))).ToList();

            default:
                return [];
        }
    }

    /// <summary>Tareas de una secuencia: una por paso, con Id determinista por inicio y paso.</summary>
    private static async Task<int> CrearSecuenciaAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Secuencia secuencia,
        RegistroAuto registro, Guid inicio)
    {
        var hoy = DateOnly.FromDateTime(now.ToOffset(FormatoPeru.ZonaHoraria).DateTime);
        var paso = 0;
        foreach (var p in secuencia.Pasos.OrderBy(p => p.Dia))
        {
            var tarea = new Tarea
            {
                Id = DeterministicId.From("secuencia_paso|" + paso++, inicio, secuencia.Id), Titulo = p.Titulo.Replace("{registro}", registro.Nombre, StringComparison.Ordinal),
                Tipo = p.Tipo, Fecha = hoy.AddDays(p.Dia), ClienteId = registro.ClienteId, ContactoId = registro.ContactoId,
                OportunidadId = registro.OportunidadId, ResponsableId = registro.ResponsableId, ResponsableNombre = registro.ResponsableNombre,
                Notas = registro.Notas, Origen = $"secuencia:{secuencia.Id}:{registro.Tipo}:{registro.Id}", CreadoEn = now,
            };
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Tarea, tarea.Id, Registros.Datos(tarea));
        }
        return paso;
    }

    /// <summary>Las tareas pendientes de una secuencia cuya oportunidad se cerró, o cuyo lead se convirtió o descartó, quedan hechas.</summary>
    private async Task<int> DetenerSecuenciasAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now)
    {
        var pendientes = await c.QueryAsync("""
            SELECT t.* FROM tareas t
            WHERE t.completada_en IS NULL AND t.origen LIKE 'secuencia:%' AND (
                  (t.origen LIKE '%:opportunity:%' AND EXISTS (SELECT 1 FROM oportunidades o WHERE o.id = substr(t.origen, -36)
                                                                AND o.etapa IN ('Ganado', 'Perdido')))
               OR (t.origen LIKE '%:lead:%' AND EXISTS (SELECT 1 FROM leads l WHERE l.id = substr(t.origen, -36)
                                                         AND l.estado IN ('convertido', 'descartado'))))
            """, Registros.LeerTarea, tx);
        var detenidas = 0;
        foreach (var tarea in pendientes.Where(t => MeToca(t.ResponsableId)))
        {
            tarea.Completar(now);
            tarea.Notas = string.Join("\n", new[] { tarea.Notas, T("secuencia.detenida") }.Where(n => !string.IsNullOrWhiteSpace(n)));
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Tarea, tarea.Id, Registros.Datos(tarea));
            detenidas++;
        }
        return detenidas;
    }

    // ---------- Registros sobre los que actúan las reglas ----------

    /// <summary>Lo que necesita una regla de una oportunidad, cotización, lead o cliente.</summary>
    internal sealed record RegistroAuto(string Tipo, Guid Id, string Nombre, Guid? ClienteId, Guid? ContactoId, Guid? OportunidadId,
        Guid? ResponsableId, string? ResponsableNombre, string? Notas = null);

    private static RegistroAuto Oportunidad(SqliteDataReader r) => new("opportunity", r.Guid("id"), r.Str("titulo"), r.GuidOrNull("cliente_id"),
        r.GuidOrNull("contacto_id"), r.Guid("id"), r.GuidOrNull("responsable_id"), r.StrOrNull("responsable_nombre"));

    private static RegistroAuto Cotizacion(SqliteDataReader r)
    {
        var numero = r.StrOrNull("numero") ?? "";
        var codigo = r.Int("version") > 1 ? $"{numero} v{r.Int("version")}" : numero;
        var nombre = r.StrOrNull("cliente") is { } cliente ? $"{codigo} · {cliente}" : codigo;
        return new("quote", r.Guid("id"), nombre, r.GuidOrNull("cliente_id"), r.GuidOrNull("contacto_id"), r.GuidOrNull("oportunidad_id"),
            r.GuidOrNull("responsable_id"), r.StrOrNull("responsable_nombre"));
    }

    private static RegistroAuto Lead(SqliteDataReader r)
    {
        var nombre = r.StrOrNull("empresa") is { Length: > 0 } empresa ? $"{empresa} ({r.Str("nombre")})" : r.Str("nombre");
        var contacto = string.Join(" · ", new[] { r.StrOrNull("telefono"), r.StrOrNull("email") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new("lead", r.Guid("id"), nombre, r.GuidOrNull("cliente_id"), r.GuidOrNull("contacto_id"), r.GuidOrNull("oportunidad_id"),
            r.GuidOrNull("responsable_id"), r.StrOrNull("responsable_nombre"), T("secuencia.nota_lead", nombre, contacto).Trim());
    }

    private static Task<RegistroAuto?> OportunidadAsync(SqliteConnection c, SqliteTransaction tx, Guid id) =>
        c.FirstOrDefaultAsync("SELECT * FROM oportunidades WHERE id = $id", Oportunidad, tx, ("$id", id));

    private static Task<RegistroAuto?> LeadAsync(SqliteConnection c, SqliteTransaction tx, Guid id) =>
        c.FirstOrDefaultAsync("SELECT * FROM leads WHERE id = $id", Lead, tx, ("$id", id));

    private static Task<RegistroAuto?> ClienteAsync(SqliteConnection c, SqliteTransaction tx, Guid id) =>
        c.FirstOrDefaultAsync("SELECT id, name, owner_id, owner_name FROM parties WHERE id = $id",
            r => new RegistroAuto("customer", r.Guid("id"), r.Str("name"), r.Guid("id"), null, null, r.GuidOrNull("owner_id"),
                r.StrOrNull("owner_name")), tx, ("$id", id));
}
