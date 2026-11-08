namespace VortexCrm.Core.Domain.Tareas;

/// <summary>
/// Notificaciones del sistema para los avisos de las tareas: en Android y en Windows son notificaciones reales; donde
/// no hay (pruebas, otras plataformas) no hacen nada y las tareas se ven en Inicio.
/// </summary>
public interface IServicioRecordatorios
{
    /// <summary>Si este dispositivo puede mostrar avisos fuera de la app.</summary>
    bool Disponible { get; }

    /// <summary>Pide permiso para mostrar notificaciones (Android 13 o superior). <c>true</c> si se podrá avisar.</summary>
    Task<bool> SolicitarPermisoAsync();

    /// <summary>Programa el aviso; si la tarea ya tenía uno, lo reemplaza.</summary>
    Task ProgramarAsync(Recordatorio recordatorio, CancellationToken cancellationToken = default);

    /// <summary>Cancela el aviso pendiente de la tarea y lo quita si ya se estaba mostrando.</summary>
    Task CancelarAsync(Guid tareaId, CancellationToken cancellationToken = default);
}

/// <summary>Donde no hay notificaciones del sistema (pruebas, otras plataformas): las tareas se ven en el panel.</summary>
public sealed class SinRecordatorios : IServicioRecordatorios
{
    public bool Disponible => false;
    public Task<bool> SolicitarPermisoAsync() => Task.FromResult(false);
    public Task ProgramarAsync(Recordatorio recordatorio, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CancelarAsync(Guid tareaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
