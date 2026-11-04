namespace VortexCrm.Core.Domain.Tareas;

/// <summary>Las tareas repartidas como se revisan en el día: vencidas, de hoy, próximas y ya hechas.</summary>
public sealed record AgendaTareas(
    IReadOnlyList<Tarea> Vencidas,
    IReadOnlyList<Tarea> Hoy,
    IReadOnlyList<Tarea> Proximas,
    IReadOnlyList<Tarea> Completadas)
{
    /// <summary>Lo que hay que atender hoy: lo de hoy más lo que quedó atrasado.</summary>
    public IReadOnlyList<Tarea> ParaHoy => [.. Vencidas, .. Hoy];

    public bool EstaVacia => Vencidas.Count + Hoy.Count + Proximas.Count + Completadas.Count == 0;

    /// <summary>Las pendientes van por fecha y hora; las hechas, primero las más recientes.</summary>
    public static AgendaTareas Organizar(IEnumerable<Tarea> tareas, DateOnly hoy)
    {
        var lista = tareas.ToList();
        var pendientes = lista.Where(t => !t.Completada).EnOrden().ToList();

        return new AgendaTareas(
            pendientes.Where(t => t.Fecha < hoy).ToList(),
            pendientes.Where(t => t.Fecha == hoy).ToList(),
            pendientes.Where(t => t.Fecha > hoy).ToList(),
            lista.Where(t => t.Completada).OrderByDescending(t => t.CompletadaEn).ToList());
    }
}

public static class OrdenTareas
{
    /// <summary>Por fecha; en el mismo día, primero las de todo el día y luego las de hora más temprana.</summary>
    public static IOrderedEnumerable<Tarea> EnOrden(this IEnumerable<Tarea> tareas) => tareas
        .OrderBy(t => t.Fecha)
        .ThenBy(t => t.Hora.HasValue)
        .ThenBy(t => t.Hora)
        .ThenBy(t => t.CreadoEn);
}
