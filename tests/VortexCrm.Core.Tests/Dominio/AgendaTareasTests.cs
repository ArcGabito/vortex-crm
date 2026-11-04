using VortexCrm.Core.Domain.Tareas;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Tareas;

public class AgendaTareasTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 5);

    [Fact]
    public void ReparteLasPendientesPorFechaYLasHechasAparte()
    {
        var hecha = new Tarea { Titulo = "Hecha", Fecha = Hoy };
        hecha.Completar(DateTimeOffset.UtcNow);

        var agenda = AgendaTareas.Organizar(
        [
            new Tarea { Titulo = "Mañana", Fecha = Hoy.AddDays(1) },
            new Tarea { Titulo = "Ayer", Fecha = Hoy.AddDays(-1) },
            new Tarea { Titulo = "Hoy", Fecha = Hoy },
            hecha,
        ], Hoy);

        Assert.Equal(["Ayer"], agenda.Vencidas.Select(t => t.Titulo));
        Assert.Equal(["Hoy"], agenda.Hoy.Select(t => t.Titulo));
        Assert.Equal(["Mañana"], agenda.Proximas.Select(t => t.Titulo));
        Assert.Equal(["Hecha"], agenda.Completadas.Select(t => t.Titulo));
        Assert.Equal(["Ayer", "Hoy"], agenda.ParaHoy.Select(t => t.Titulo));
    }

    [Fact]
    public void EnElMismoDiaVanPrimeroLasDeTodoElDiaYLuegoPorHora()
    {
        var agenda = AgendaTareas.Organizar(
        [
            new Tarea { Titulo = "Tarde", Fecha = Hoy, Hora = new TimeOnly(16, 0) },
            new Tarea { Titulo = "Mañana temprano", Fecha = Hoy, Hora = new TimeOnly(8, 30) },
            new Tarea { Titulo = "Todo el día", Fecha = Hoy },
        ], Hoy);

        Assert.Equal(["Todo el día", "Mañana temprano", "Tarde"], agenda.Hoy.Select(t => t.Titulo));
    }

    [Fact]
    public void LasHechasMasRecientesVanPrimero()
    {
        var antigua = new Tarea { Titulo = "Antigua" };
        antigua.Completar(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var reciente = new Tarea { Titulo = "Reciente" };
        reciente.Completar(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

        var agenda = AgendaTareas.Organizar([antigua, reciente], Hoy);

        Assert.Equal(["Reciente", "Antigua"], agenda.Completadas.Select(t => t.Titulo));
    }

    [Fact]
    public void SinTareasEstaVacia() => Assert.True(AgendaTareas.Organizar([], Hoy).EstaVacia);
}
