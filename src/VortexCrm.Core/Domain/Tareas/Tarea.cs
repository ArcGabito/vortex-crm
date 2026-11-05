using VortexCrm.Core.Domain.Comun;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Tareas;

public enum TipoTarea
{
    Llamada,
    WhatsApp,
    Reunion,
    Visita,
    Correo,
    Otra,
}

public static class TiposTarea
{
    public static IReadOnlyList<TipoTarea> Todos { get; } = Enum.GetValues<TipoTarea>();

    public static string Nombre(this TipoTarea tipo) => T("enum.tarea." + tipo.ToString().ToLowerInvariant());
}

/// <summary>Algo que hay que hacer en una fecha: llamar a un cliente, mandarle la cotización, visitarlo.</summary>
public sealed class Tarea
{
    /// <summary>Las tareas sin hora se avisan a esta hora del día.</summary>
    public static readonly TimeOnly HoraDeAvisoSinHora = new(9, 0);

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Titulo { get; set; } = "";

    public TipoTarea Tipo { get; set; } = TipoTarea.Otra;

    public DateOnly Fecha { get; set; } = FormatoPeru.Hoy();

    /// <summary>Hora de Perú. Sin hora, la tarea es para cualquier momento del día.</summary>
    public TimeOnly? Hora { get; set; }

    /// <summary>Con cuántos minutos de anticipación avisar; <c>null</c> si no se quiere aviso.</summary>
    public int? AvisarMinutosAntes { get; set; }

    /// <summary>Cliente (empresa o persona del hub) relacionado, si lo hay.</summary>
    public Guid? ClienteId { get; set; }

    public Guid? ContactoId { get; set; }

    /// <summary>Oportunidad del pipeline a la que corresponde el seguimiento, si la hay.</summary>
    public Guid? OportunidadId { get; set; }

    /// <summary>Usuario del hub responsable de la tarea: sus avisos solo suenan en sus equipos.</summary>
    public Guid? ResponsableId { get; set; }

    public string? ResponsableNombre { get; set; }

    public string? Notas { get; set; }

    public DateTimeOffset CreadoEn { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletadaEn { get; internal set; }

    public bool Completada => CompletadaEn is not null;

    /// <summary>Cuándo toca la tarea, en hora de Perú. Sin hora, se toma la hora de aviso por defecto.</summary>
    public DateTimeOffset Momento => new(Fecha.ToDateTime(Hora ?? HoraDeAvisoSinHora), FormatoPeru.ZonaHoraria);

    /// <summary>Cuándo avisar, o <c>null</c> si la tarea no tiene aviso.</summary>
    public DateTimeOffset? MomentoDelAviso => AvisarMinutosAntes is { } minutos ? Momento.AddMinutes(-minutos) : null;

    /// <summary>Marcarla otra vez como hecha no cambia la fecha en que se completó.</summary>
    public void Completar(DateTimeOffset ahora) => CompletadaEn ??= ahora;

    public void Reabrir() => CompletadaEn = null;

    /// <summary>Sigue pendiente y su fecha ya pasó.</summary>
    public bool EstaVencida(DateOnly hoy) => !Completada && Fecha < hoy;

    /// <summary>
    /// La notificación que hay que programar, o <c>null</c> si no corresponde: la tarea no
    /// tiene aviso, ya está hecha o el momento del aviso ya pasó.
    /// </summary>
    public Recordatorio? CrearRecordatorio(DateTimeOffset ahora)
    {
        if (Completada || MomentoDelAviso is not { } aviso || aviso <= ahora)
        {
            return null;
        }

        return new Recordatorio(Id, Titulo, MensajeDelAviso(aviso), aviso);
    }

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();

        if (string.IsNullOrWhiteSpace(Titulo))
        {
            errores.Add(T("error.escribe_que_hay_que_hacer"));
        }

        if (AvisarMinutosAntes < 0)
        {
            errores.Add(T("error.el_aviso_no_puede_ser"));
        }

        return errores;
    }

    public Tarea Clonar() => (Tarea)MemberwiseClone();

    /// <summary>"Llamada · Hoy a las 10:30 a. m.", "Mañana a la 1:00 p. m.", "WhatsApp · Para hoy".</summary>
    private string MensajeDelAviso(DateTimeOffset aviso)
    {
        var diaDelAviso = DateOnly.FromDateTime(aviso.ToOffset(FormatoPeru.ZonaHoraria).DateTime);
        var cuando = FormatoPeru.FechaRelativa(Fecha, diaDelAviso);

        var texto = Hora is { } hora
            ? $"{cuando} {(hora.Hour % 12 == 1 ? "a la" : "a las")} {FormatoPeru.Hora(hora)}"
            : $"Para {cuando.ToLower(FormatoPeru.Cultura)}";

        return Tipo == TipoTarea.Otra ? texto : $"{Tipo.Nombre()} · {texto}";
    }
}

/// <summary>Una notificación programada para avisar de una tarea.</summary>
public sealed record Recordatorio(Guid TareaId, string Titulo, string Mensaje, DateTimeOffset Momento);

public sealed record OpcionAviso(int MinutosAntes, string Nombre);

public static class Avisos
{
    /// <summary>
    /// Las anticipaciones que tienen sentido: con hora, desde "a la hora" hasta un día antes;
    /// sin hora, el mismo día o el día anterior, a la hora de aviso por defecto.
    /// </summary>
    public static IReadOnlyList<OpcionAviso> Para(TimeOnly? hora)
    {
        var horaDeAviso = FormatoPeru.Hora(Tarea.HoraDeAvisoSinHora);

        return hora is null
            ?
            [
                new(0, T("aviso.mismo_dia", horaDeAviso)),
                new(24 * 60, T("aviso.dia_antes_hora", horaDeAviso)),
            ]
            :
            [
                new(0, T("aviso.a_la_hora")),
                new(15, T("aviso.15_minutos_antes")),
                new(60, T("aviso.1_hora_antes")),
                new(24 * 60, T("aviso.1_dia_antes")),
            ];
    }
}
