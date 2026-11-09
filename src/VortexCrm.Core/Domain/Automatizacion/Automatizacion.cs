using VortexCrm.Core.Domain.Tareas;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Automatizacion;

/// <summary>Qué hace saltar una regla (CRM-61). Cada uno mira un tipo de registro y usa <see cref="Regla.Dias"/>.</summary>
public enum DisparadorRegla
{
    /// <summary>Oportunidad abierta sin movimiento (cambios, interacciones ni tareas hechas) hace <c>Dias</c> días (CRM-26).</summary>
    OportunidadEstancada,

    /// <summary>Cotización enviada hace <c>Dias</c> días sin respuesta del cliente.</summary>
    CotizacionSinRespuesta,

    /// <summary>Cotización enviada que vence dentro de <c>Dias</c> días.</summary>
    CotizacionPorVencer,

    /// <summary>Lead nuevo sin contactar desde hace <c>Dias</c> días.</summary>
    LeadSinAtender,

    /// <summary>Oportunidad ganada hace <c>Dias</c> días (seguimiento de posventa).</summary>
    OportunidadGanada,
}

public enum AccionRegla
{
    /// <summary>Crea una tarea al responsable del registro.</summary>
    CrearTarea,

    /// <summary>Inicia una secuencia de seguimiento sobre el registro.</summary>
    IniciarSecuencia,
}

/// <summary>
/// Regla de automatización: si pasa <see cref="Disparador"/>, hace <see cref="Accion"/>. Salta una sola vez por registro y
/// situación (una oportunidad que vuelve a quedarse quieta después de moverse vuelve a avisar), aunque haya varios equipos
/// abiertos: cada disparo es un registro replicado con Id determinista.
/// </summary>
public sealed class Regla
{
    /// <summary>Id de la regla predeterminada de oportunidades estancadas (CRM-26), que rige mientras no haya reglas guardadas.</summary>
    public static readonly Guid EstancadaPredeterminada = Guid.Parse("5e7a0c3d-0000-4000-8000-000000000015");

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Nombre { get; set; } = "";
    public DisparadorRegla Disparador { get; set; } = DisparadorRegla.OportunidadEstancada;
    public int Dias { get; set; } = 15;
    public AccionRegla Accion { get; set; } = AccionRegla.CrearTarea;

    /// <summary>Título de la tarea; <c>{registro}</c> se reemplaza por la oportunidad, cotización o lead.</summary>
    public string? TituloTarea { get; set; }

    public TipoTarea TipoTarea { get; set; } = TipoTarea.Llamada;

    /// <summary>Para cuántos días después de saltar queda la tarea (0: hoy).</summary>
    public int DiasParaVencer { get; set; }

    public Guid? SecuenciaId { get; set; }
    public bool Activa { get; set; } = true;

    public static Regla Predeterminada() => new()
    {
        Id = EstancadaPredeterminada, Nombre = T("regla.estancada_nombre"), Disparador = DisparadorRegla.OportunidadEstancada, Dias = 15,
        TituloTarea = T("regla.estancada_tarea"), TipoTarea = TipoTarea.Llamada,
    };

    public IReadOnlyList<string> Validar(IReadOnlyCollection<Guid> secuencias)
    {
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Nombre))
            errores.Add(T("error.regla_sin_nombre"));
        if (Dias is < 0 or > 365 || DiasParaVencer is < 0 or > 365)
            errores.Add(T("error.regla_dias"));
        if (Accion == AccionRegla.IniciarSecuencia && (SecuenciaId is not { } s || !secuencias.Contains(s)))
            errores.Add(T("error.regla_sin_secuencia"));
        if (Accion == AccionRegla.IniciarSecuencia && Disparador is DisparadorRegla.CotizacionSinRespuesta or DisparadorRegla.CotizacionPorVencer)
            errores.Add(T("error.regla_secuencia_cotizacion"));
        return errores;
    }

    public string Titulo(string registro) =>
        (string.IsNullOrWhiteSpace(TituloTarea) ? Nombre : TituloTarea).Replace("{registro}", registro, StringComparison.Ordinal).Trim();

    public static string NombreDisparador(DisparadorRegla d) => T("enum.disparador." + d.ToString().ToLowerInvariant());
}

/// <summary>Un paso de una secuencia: qué tarea crear y cuántos días después de iniciarla.</summary>
public sealed record PasoSecuencia(int Dia, TipoTarea Tipo, string Titulo);

/// <summary>
/// Secuencia de seguimiento (CRM-46): tareas con plazos (día 0 llamar, día 3 WhatsApp, día 7 visita…) que se crean al
/// iniciarla sobre una oportunidad, un lead o un cliente. Si la oportunidad se cierra o el lead se convierte o descarta,
/// las tareas que faltan se dan por hechas solas.
/// </summary>
public sealed class Secuencia
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Nombre { get; set; } = "";
    public List<PasoSecuencia> Pasos { get; set; } = [];

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Nombre))
            errores.Add(T("error.secuencia_sin_nombre"));
        if (Pasos.Count == 0 || Pasos.Any(p => string.IsNullOrWhiteSpace(p.Titulo)))
            errores.Add(T("error.secuencia_sin_pasos"));
        if (Pasos.Any(p => p.Dia is < 0 or > 365))
            errores.Add(T("error.regla_dias"));
        return errores;
    }
}

/// <summary>Cómo se reparte un lead nuevo sin responsable (CRM-12).</summary>
public enum ModoAsignacion
{
    /// <summary>Queda para quien lo registra.</summary>
    Ninguna,

    /// <summary>Por turno entre los vendedores elegidos.</summary>
    Turno,

    /// <summary>Al vendedor elegido con menos leads abiertos.</summary>
    Carga,
}

public sealed record AsignacionLeads(ModoAsignacion Modo, IReadOnlyList<Vendedor> Vendedores)
{
    public static readonly AsignacionLeads Ninguna = new(ModoAsignacion.Ninguna, []);

    public bool Activa => Modo != ModoAsignacion.Ninguna && Vendedores.Count > 0;
}

/// <summary>Una ejecución de las reglas: cuántas tareas creó y cuántas secuencias inició o detuvo.</summary>
public sealed record ResultadoAutomatizacion(int Tareas, int Secuencias, int Detenidas)
{
    public int Total => Tareas + Secuencias + Detenidas;
}
