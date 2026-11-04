using VortexCrm.Core.Domain.Tareas;
using Xunit;

namespace VortexCrm.Core.Tests.Dominio.Tareas;

public class TareaTests
{
    private static readonly DateOnly Lunes = new(2026, 10, 5);

    /// <summary>Domingo 4 de octubre, 8:00 a. m. en Lima.</summary>
    private static readonly DateTimeOffset Antes = new(2026, 10, 4, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ElMomentoEsLaHoraDePeru()
    {
        var tarea = new Tarea { Fecha = Lunes, Hora = new TimeOnly(10, 30) };

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 15, 30, 0, TimeSpan.Zero), tarea.Momento);
    }

    [Fact]
    public void SinHoraSeAvisaALasNueve()
    {
        var tarea = new Tarea { Fecha = Lunes, AvisarMinutosAntes = 0 };

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.Zero), tarea.MomentoDelAviso);
    }

    [Fact]
    public void ElAvisoSeAdelantaLosMinutosElegidos()
    {
        var tarea = new Tarea { Titulo = "Llamar a Rosa", Fecha = Lunes, Hora = new TimeOnly(10, 30), AvisarMinutosAntes = 15 };

        var recordatorio = tarea.CrearRecordatorio(Antes);

        Assert.NotNull(recordatorio);
        Assert.Equal(tarea.Id, recordatorio.TareaId);
        Assert.Equal("Llamar a Rosa", recordatorio.Titulo);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 15, 15, 0, TimeSpan.Zero), recordatorio.Momento);
    }

    [Fact]
    public void NoHayRecordatorioSinAvisoHechaOSiYaPaso()
    {
        var sinAviso = new Tarea { Fecha = Lunes };
        Assert.Null(sinAviso.CrearRecordatorio(Antes));

        var hecha = new Tarea { Fecha = Lunes, AvisarMinutosAntes = 0 };
        hecha.Completar(Antes);
        Assert.Null(hecha.CrearRecordatorio(Antes));

        var pasada = new Tarea { Fecha = Lunes, AvisarMinutosAntes = 0 };
        Assert.Null(pasada.CrearRecordatorio(pasada.Momento));
        Assert.NotNull(pasada.CrearRecordatorio(pasada.Momento.AddMinutes(-1)));
    }

    [Theory]
    [InlineData(TipoTarea.Llamada, 10, 30, 15, "Llamada · Hoy a las 10:30 a. m.")]
    [InlineData(TipoTarea.Otra, 13, 0, 24 * 60, "Mañana a la 1:00 p. m.")]
    [InlineData(TipoTarea.Reunion, 16, 0, 60, "Reunión · Hoy a las 4:00 p. m.")]
    public void ElMensajeDiceCuandoEsLaTarea(TipoTarea tipo, int hora, int minuto, int minutosAntes, string esperado)
    {
        var tarea = new Tarea { Tipo = tipo, Fecha = Lunes, Hora = new TimeOnly(hora, minuto), AvisarMinutosAntes = minutosAntes };

        Assert.Equal(esperado, tarea.CrearRecordatorio(Antes)!.Mensaje);
    }

    [Fact]
    public void SinHoraElMensajeDiceParaQueDia()
    {
        var tarea = new Tarea { Tipo = TipoTarea.WhatsApp, Fecha = Lunes, AvisarMinutosAntes = 24 * 60 };

        Assert.Equal("WhatsApp · Para mañana", tarea.CrearRecordatorio(Antes)!.Mensaje);
    }

    [Fact]
    public void CompletarGuardaLaPrimeraFechaYReabrirLaBorra()
    {
        var tarea = new Tarea();

        tarea.Completar(Antes);
        tarea.Completar(Antes.AddDays(1));
        Assert.Equal(Antes, tarea.CompletadaEn);

        tarea.Reabrir();
        Assert.False(tarea.Completada);
    }

    [Fact]
    public void SoloEstaVencidaSiSiguePendienteYPasoLaFecha()
    {
        var tarea = new Tarea { Fecha = Lunes };

        Assert.False(tarea.EstaVencida(Lunes));
        Assert.True(tarea.EstaVencida(Lunes.AddDays(1)));

        tarea.Completar(Antes);
        Assert.False(tarea.EstaVencida(Lunes.AddDays(1)));
    }

    [Fact]
    public void ValidarExigeElTitulo()
    {
        var tarea = new Tarea();

        Assert.Single(tarea.Validar());

        tarea.Titulo = "Llamar a Rosa";
        Assert.Empty(tarea.Validar());
    }

    [Fact]
    public void LasOpcionesDeAvisoDependenDeSiHayHora()
    {
        Assert.Equal([0, 24 * 60], Avisos.Para(null).Select(o => o.MinutosAntes));
        Assert.Contains("9:00 a. m.", Avisos.Para(null)[0].Nombre);
        Assert.Equal([0, 15, 60, 24 * 60], Avisos.Para(new TimeOnly(10, 0)).Select(o => o.MinutosAntes));
    }
}
