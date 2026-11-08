using System.Collections.Concurrent;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using VortexCrm.Core.Domain.Tareas;

namespace VortexCrm.Platforms.Windows;

/// <summary>
/// Avisos de tareas como notificaciones de Windows (Windows App SDK). Windows no programa notificaciones para una app
/// sin empaquetar, así que la app revisa cada medio minuto los avisos pendientes y los muestra a su hora; funciona
/// mientras la app está abierta (también minimizada). Al abrirla se vuelven a programar
/// (<c>ServicioTareas.ReprogramarAvisosAsync</c>), igual que en Android después de reiniciar.
/// </summary>
public sealed class ServicioRecordatoriosWindows : IServicioRecordatorios, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Recordatorio> _pendientes = new();
    private readonly Timer _revision;
    private bool _registrado;

    public ServicioRecordatoriosWindows() =>
        _revision = new Timer(_ => Revisar(DateTimeOffset.UtcNow), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

    public bool Disponible
    {
        get
        {
            try
            {
                return AppNotificationManager.IsSupported();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<bool> SolicitarPermisoAsync()
    {
        Registrar();
        return Task.FromResult(_registrado);
    }

    public Task ProgramarAsync(Recordatorio recordatorio, CancellationToken cancellationToken = default)
    {
        Registrar();
        _pendientes[recordatorio.TareaId] = recordatorio;
        return Task.CompletedTask;
    }

    public async Task CancelarAsync(Guid tareaId, CancellationToken cancellationToken = default)
    {
        _pendientes.TryRemove(tareaId, out _);
        if (!_registrado)
            return;
        try
        {
            // Si el aviso ya se estaba mostrando (por ejemplo, la tarea se acaba de completar), se quita
            await AppNotificationManager.Default.RemoveByTagAsync(tareaId.ToString());
        }
        catch (Exception)
        {
            // Sin centro de notificaciones no hay nada que quitar
        }
    }

    /// <summary>Muestra los avisos que ya llegaron a su hora (una sola vez cada uno).</summary>
    internal void Revisar(DateTimeOffset ahora)
    {
        foreach (var recordatorio in _pendientes.Values.Where(r => r.Momento <= ahora).ToList())
        {
            if (_pendientes.TryRemove(recordatorio.TareaId, out _))
                Mostrar(recordatorio);
        }
    }

    private void Registrar()
    {
        if (_registrado)
            return;
        try
        {
            AppNotificationManager.Default.Register();
            _registrado = true;
        }
        catch (Exception)
        {
            _registrado = false;
        }
    }

    private void Mostrar(Recordatorio recordatorio)
    {
        if (!_registrado)
            return;
        try
        {
            var aviso = new AppNotificationBuilder().AddText(recordatorio.Titulo).AddText(recordatorio.Mensaje).BuildNotification();
            aviso.Tag = recordatorio.TareaId.ToString();
            AppNotificationManager.Default.Show(aviso);
        }
        catch (Exception)
        {
            // Si Windows no deja mostrarlo, la tarea sigue en el panel
        }
    }

    public void Dispose()
    {
        _revision.Dispose();
        if (_registrado)
        {
            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception)
            {
                // La app se está cerrando
            }
        }
    }
}
