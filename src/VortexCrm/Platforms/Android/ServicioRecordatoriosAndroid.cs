using Android.App;
using Android.Content;
using AndroidX.Core.App;
using VortexCrm.Core.Domain.Tareas;

namespace VortexCrm.Platforms.Android;

/// <summary>
/// Avisos de tareas como notificaciones de Android. Se programan con AlarmManager sin hora
/// exacta: el sistema puede retrasarlos unos minutos para ahorrar batería, pero así no hace
/// falta el permiso de alarmas exactas, que Android 14 niega por defecto.
/// </summary>
/// <remarks>Android borra las alarmas al reiniciar el equipo: la app las vuelve a programar al abrirse (<c>ServicioTareas.ReprogramarAvisosAsync</c>).</remarks>
public sealed class ServicioRecordatoriosAndroid : IServicioRecordatorios
{
    public bool Disponible => true;

    public async Task<bool> SolicitarPermisoAsync()
    {
        // Antes de Android 13 no hace falta pedir permiso y siempre devuelve Granted
        var estado = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PostNotifications>);
        return estado == PermissionStatus.Granted;
    }

    public Task ProgramarAsync(Recordatorio recordatorio, CancellationToken cancellationToken = default)
    {
        var contexto = Platform.AppContext;
        var intent = ReceptorRecordatorios.CrearIntent(contexto, recordatorio.TareaId)
            .PutExtra(ReceptorRecordatorios.ExtraTitulo, recordatorio.Titulo)!
            .PutExtra(ReceptorRecordatorios.ExtraMensaje, recordatorio.Mensaje)!;

        var pendiente = PendingIntent.GetBroadcast(
            contexto,
            ReceptorRecordatorios.CodigoDe(recordatorio.TareaId),
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

        Alarmas(contexto).SetAndAllowWhileIdle(AlarmType.RtcWakeup, recordatorio.Momento.ToUnixTimeMilliseconds(), pendiente);
        return Task.CompletedTask;
    }

    public Task CancelarAsync(Guid tareaId, CancellationToken cancellationToken = default)
    {
        var contexto = Platform.AppContext;
        var pendiente = PendingIntent.GetBroadcast(
            contexto,
            ReceptorRecordatorios.CodigoDe(tareaId),
            ReceptorRecordatorios.CrearIntent(contexto, tareaId),
            PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable);

        if (pendiente is not null)
        {
            Alarmas(contexto).Cancel(pendiente);
            pendiente.Cancel();
        }

        // Si el aviso ya se estaba mostrando (por ejemplo, la tarea se acaba de completar), se quita
        NotificationManagerCompat.From(contexto)!.Cancel(ReceptorRecordatorios.CodigoDe(tareaId));
        return Task.CompletedTask;
    }

    private static AlarmManager Alarmas(Context contexto) => (AlarmManager)contexto.GetSystemService(Context.AlarmService)!;
}

/// <summary>Recibe la alarma de una tarea y muestra su notificación; al tocarla se abre la app.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class ReceptorRecordatorios : BroadcastReceiver
{
    public const string ExtraTitulo = "titulo";
    public const string ExtraMensaje = "mensaje";

    private const string Canal = "recordatorios";

    /// <summary>
    /// El Id de la tarea va en la dirección del intent: Android distingue las alarmas por
    /// ella (no por los extras), así cada tarea tiene la suya y se puede cancelar.
    /// </summary>
    public static Intent CrearIntent(Context contexto, Guid tareaId) =>
        new Intent(contexto, typeof(ReceptorRecordatorios))
            .SetData(Android.Net.Uri.Parse($"vortexcrm://tareas/{tareaId}"))!;

    /// <summary>Número de la notificación de la tarea (el hash de un Guid es siempre el mismo).</summary>
    public static int CodigoDe(Guid tareaId) => tareaId.GetHashCode();

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Data?.LastPathSegment is not { } texto || !Guid.TryParse(texto, out var tareaId))
        {
            return;
        }

        var notificaciones = NotificationManagerCompat.From(context)!;
        if (!notificaciones.AreNotificationsEnabled())
        {
            return;
        }

        CrearCanal(context);

        var abrirApp = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        abrirApp?.AddFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
        var alTocar = abrirApp is null
            ? null
            : PendingIntent.GetActivity(context, CodigoDe(tareaId), abrirApp, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var notificacion = new NotificationCompat.Builder(context, Canal)
            .SetSmallIcon(Resource.Drawable.ic_notificacion)!
            .SetColor(Android.Graphics.Color.ParseColor("#512BD4"))!
            .SetContentTitle(intent.GetStringExtra(ExtraTitulo))!
            .SetContentText(intent.GetStringExtra(ExtraMensaje))!
            .SetCategory(NotificationCompat.CategoryReminder)!
            .SetPriority(NotificationCompat.PriorityHigh)!
            .SetAutoCancel(true)!
            .SetContentIntent(alTocar)!
            .Build()!;

        notificaciones.Notify(CodigoDe(tareaId), notificacion);
    }

    private static void CrearCanal(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        // Crear un canal que ya existe no hace nada, así que se puede llamar siempre
        var canal = new NotificationChannel(Canal, "Recordatorios de tareas", NotificationImportance.High)
        {
            Description = "Avisos de las tareas que programas en Vortex CRM",
        };
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(canal);
    }
}
