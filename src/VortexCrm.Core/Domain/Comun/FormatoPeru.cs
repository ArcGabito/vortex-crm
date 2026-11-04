using System.Globalization;

namespace VortexCrm.Core.Domain.Comun;

/// <summary>Formatos de moneda y fecha para Perú, iguales en cualquier dispositivo.</summary>
public static class FormatoPeru
{
    /// <summary>Hora de Perú (UTC-5). Perú no usa horario de verano.</summary>
    public static readonly TimeSpan ZonaHoraria = TimeSpan.FromHours(-5);

    /// <summary>
    /// Cultura es-PE con el formato de números fijado ("S/ 1,234.50"), para no depender
    /// de los datos regionales del sistema operativo.
    /// </summary>
    public static CultureInfo Cultura { get; } = CrearCultura();

    /// <summary>Formatea un monto en soles: <c>S/ 1,234.50</c>.</summary>
    public static string Soles(decimal monto) => monto.ToString("C2", Cultura);

    /// <summary>La fecha de hoy en Perú, sin importar la zona horaria del servidor o del equipo.</summary>
    public static DateOnly Hoy(TimeProvider? reloj = null) =>
        DateOnly.FromDateTime((reloj ?? TimeProvider.System).GetUtcNow().ToOffset(ZonaHoraria).DateTime);

    /// <summary>Formatea una hora como se usa en Perú: <c>9:00 a. m.</c>, <c>4:30 p. m.</c></summary>
    public static string Hora(TimeOnly hora) => hora.ToString("h:mm tt", Cultura);

    /// <summary>"Hoy", "Mañana" o "Ayer"; si no, la fecha corta ("lun. 5 oct."), con el año si no es el actual.</summary>
    public static string FechaRelativa(DateOnly fecha, DateOnly hoy) => (fecha.DayNumber - hoy.DayNumber) switch
    {
        0 => "Hoy",
        1 => "Mañana",
        -1 => "Ayer",
        _ when fecha.Year == hoy.Year => fecha.ToString("ddd d MMM", Cultura),
        _ => fecha.ToString("d MMM yyyy", Cultura),
    };

    private static CultureInfo CrearCultura()
    {
        CultureInfo cultura;
        try
        {
            cultura = (CultureInfo)CultureInfo.GetCultureInfo("es-PE").Clone();
        }
        catch (CultureNotFoundException)
        {
            // Con globalización invariante no hay datos de es-PE; el formato de números igual queda fijo
            cultura = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        }

        var numeros = cultura.NumberFormat;
        numeros.CurrencySymbol = "S/";
        numeros.CurrencyDecimalDigits = 2;
        numeros.CurrencyDecimalSeparator = ".";
        numeros.CurrencyGroupSeparator = ",";
        numeros.CurrencyPositivePattern = 2; // S/ n
        numeros.CurrencyNegativePattern = 12; // S/ -n
        numeros.NumberDecimalSeparator = ".";
        numeros.NumberGroupSeparator = ",";
        numeros.PercentDecimalSeparator = ".";
        numeros.PercentGroupSeparator = ",";
        numeros.PercentPositivePattern = 1; // n%
        numeros.PercentNegativePattern = 1; // -n%

        cultura.DateTimeFormat.AMDesignator = "a. m.";
        cultura.DateTimeFormat.PMDesignator = "p. m.";

        return CultureInfo.ReadOnly(cultura);
    }
}
