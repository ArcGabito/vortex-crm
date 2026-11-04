using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Services;

/// <summary>
/// Tareas de seguimiento (llamar, visitar, enviar la cotización). Cada una tiene un responsable (usuario del hub): sus
/// avisos suenan en los equipos de esa persona. Se replican a los demás equipos del CRM.
/// </summary>
public sealed class ServicioTareas(CrmDatabase db, ICrmContext context, TimeProvider clock, IServicioRecordatorios recordatorios)
    : CrmService(db, context, clock)
{
    /// <param name="soloMias">Solo las del usuario actual (y las que no tienen responsable).</param>
    public async Task<IReadOnlyList<TareaVista>> ListarAsync(bool soloMias = false, Guid? clienteId = null, Guid? oportunidadId = null,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT t.*, p.name AS cliente, o.titulo AS oportunidad
            FROM tareas t LEFT JOIN parties p ON p.id = t.cliente_id LEFT JOIN oportunidades o ON o.id = t.oportunidad_id
            WHERE ($mias = 0 OR t.responsable_id IS NULL OR t.responsable_id = $yo)
              AND ($cliente IS NULL OR t.cliente_id = $cliente) AND ($oportunidad IS NULL OR t.oportunidad_id = $oportunidad)
            ORDER BY t.fecha, t.hora
            """, r => new TareaVista(Registros.LeerTarea(r), r.StrOrNull("cliente"), r.StrOrNull("oportunidad")), null,
            ("$mias", soloMias), ("$yo", Context.UserId), ("$cliente", clienteId), ("$oportunidad", oportunidadId));
    }

    public async Task<Tarea?> ObtenerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ObtenerAsync(c, null, id);
    }

    /// <summary>Una tarea nueva asignada al usuario actual.</summary>
    public Tarea Nueva(Guid? clienteId = null, Guid? oportunidadId = null) => new()
    {
        ClienteId = clienteId, OportunidadId = oportunidadId, ResponsableId = Context.UserId, ResponsableNombre = Context.UserName,
        Fecha = Domain.Comun.FormatoPeru.Hoy(Clock), CreadoEn = Clock.GetUtcNow(),
    };

    public async Task GuardarAsync(Tarea tarea, CancellationToken ct = default)
    {
        var errores = tarea.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        await InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Tarea, tarea.Id, Registros.Datos(tarea));
            return 0;
        }, ct);
        await ActualizarAvisoAsync(tarea, ct);
    }

    public Task CompletarAsync(Guid id, CancellationToken ct = default) => CambiarAsync(id, (t, now) => t.Completar(now), ct);

    public Task ReabrirAsync(Guid id, CancellationToken ct = default) => CambiarAsync(id, (t, _) => t.Reabrir(), ct);

    public async Task EliminarAsync(Guid id, CancellationToken ct = default)
    {
        await InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Tarea, id);
            return 0;
        }, ct);
        await recordatorios.CancelarAsync(id, ct);
    }

    /// <summary>
    /// Vuelve a programar los avisos de las tareas pendientes del usuario (al iniciar sesión, al sincronizar y después de
    /// reiniciar el equipo, que borra las alarmas).
    /// </summary>
    public async Task ReprogramarAvisosAsync(CancellationToken ct = default)
    {
        if (!recordatorios.Disponible)
            return;
        foreach (var vista in await ListarAsync(soloMias: true, ct: ct))
            await ActualizarAvisoAsync(vista.Tarea, ct);
    }

    internal static Task<Tarea?> ObtenerAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync("SELECT * FROM tareas WHERE id = $id", Registros.LeerTarea, tx, ("$id", id));

    private async Task CambiarAsync(Guid id, Action<Tarea, DateTimeOffset> cambio, CancellationToken ct)
    {
        var tarea = await InTransactionAsync(async (c, tx, now) =>
        {
            var t = await ObtenerAsync(c, tx, id);
            Require(t is not null, "La tarea no existe.");
            cambio(t!, now);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Tarea, t!.Id, Registros.Datos(t));
            return t;
        }, ct);
        await ActualizarAvisoAsync(tarea, ct);
    }

    /// <summary>Programa el aviso si es del usuario y corresponde; si no, lo cancela.</summary>
    private async Task ActualizarAvisoAsync(Tarea tarea, CancellationToken ct)
    {
        var mia = tarea.ResponsableId is null || tarea.ResponsableId == Context.UserId;
        if (mia && tarea.CrearRecordatorio(Clock.GetUtcNow()) is { } recordatorio)
            await recordatorios.ProgramarAsync(recordatorio, ct);
        else
            await recordatorios.CancelarAsync(tarea.Id, ct);
    }
}
