namespace VortexCrm.Core.Domain.Comun;

/// <summary>
/// Monto en letras, como se escribe en cotizaciones y comprobantes peruanos:
/// <c>MIL QUINIENTOS CON 00/100 SOLES</c>.
/// </summary>
public static class MontoEnLetras
{
    private static readonly string[] Unidades =
    [
        "", "UNO", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE", "OCHO", "NUEVE",
        "DIEZ", "ONCE", "DOCE", "TRECE", "CATORCE", "QUINCE", "DIECISÉIS", "DIECISIETE", "DIECIOCHO", "DIECINUEVE",
        "VEINTE", "VEINTIUNO", "VEINTIDÓS", "VEINTITRÉS", "VEINTICUATRO", "VEINTICINCO", "VEINTISÉIS", "VEINTISIETE", "VEINTIOCHO", "VEINTINUEVE",
    ];

    private static readonly string[] Decenas =
        ["", "", "", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA", "SETENTA", "OCHENTA", "NOVENTA"];

    private static readonly string[] Centenas =
        ["", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS", "QUINIENTOS", "SEISCIENTOS", "SETECIENTOS", "OCHOCIENTOS", "NOVECIENTOS"];

    public static string Soles(decimal monto)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monto);

        var redondeado = Math.Round(monto, 2, MidpointRounding.AwayFromZero);
        var entero = (long)Math.Truncate(redondeado);
        var centimos = (int)((redondeado - entero) * 100);

        return $"{Numero(entero)} CON {centimos:00}/100 SOLES";
    }

    /// <summary>
    /// Monto en letras en la moneda indicada: <c>MIL CON 50/100 PESOS MEXICANOS</c>, <c>CIEN CON 00/100 DÓLARES
    /// AMERICANOS</c>. Las monedas sin decimales (peso chileno) no llevan la fracción.
    /// </summary>
    public static string Monto(decimal monto, string moneda)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monto);
        var codigo = moneda.ToUpperInvariant();
        var nombre = NombresDeMoneda.GetValueOrDefault(codigo, codigo);
        if (VortexApi.Suite.Money.Currencies.Get(codigo).Decimals == 0)
            return $"{Numero((long)Math.Round(monto, 0, MidpointRounding.AwayFromZero))} {nombre}";
        var redondeado = Math.Round(monto, 2, MidpointRounding.AwayFromZero);
        var entero = (long)Math.Truncate(redondeado);
        return $"{Numero(entero)} CON {(int)((redondeado - entero) * 100):00}/100 {nombre}";
    }

    private static readonly Dictionary<string, string> NombresDeMoneda = new()
    {
        ["PEN"] = "SOLES", ["USD"] = "DÓLARES AMERICANOS", ["EUR"] = "EUROS", ["MXN"] = "PESOS MEXICANOS",
        ["COP"] = "PESOS COLOMBIANOS", ["CLP"] = "PESOS CHILENOS", ["ARS"] = "PESOS ARGENTINOS", ["BOB"] = "BOLIVIANOS",
        ["UYU"] = "PESOS URUGUAYOS", ["PYG"] = "GUARANÍES", ["CRC"] = "COLONES", ["GTQ"] = "QUETZALES", ["DOP"] = "PESOS DOMINICANOS",
        ["HNL"] = "LEMPIRAS", ["NIO"] = "CÓRDOBAS", ["PAB"] = "BALBOAS", ["BRL"] = "REALES", ["GBP"] = "LIBRAS ESTERLINAS",
    };

    public static string Numero(long numero)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numero);

        if (numero == 0)
        {
            return "CERO";
        }

        var partes = new List<string>();
        var millones = numero / 1_000_000;
        var miles = (int)(numero / 1_000 % 1_000);
        var resto = (int)(numero % 1_000);

        if (millones > 0)
        {
            partes.Add(millones == 1 ? "UN MILLÓN" : $"{Apocopar(Numero(millones))} MILLONES");
        }

        if (miles > 0)
        {
            partes.Add(miles == 1 ? "MIL" : $"{Apocopar(HastaMil(miles))} MIL");
        }

        if (resto > 0)
        {
            partes.Add(HastaMil(resto));
        }

        return string.Join(" ", partes);
    }

    /// <summary>De 1 a 999.</summary>
    private static string HastaMil(int numero)
    {
        if (numero == 100)
        {
            return "CIEN";
        }

        var centenas = Centenas[numero / 100];
        var resto = numero % 100;
        return resto == 0 ? centenas : $"{centenas} {HastaCien(resto)}".Trim();
    }

    /// <summary>De 1 a 99.</summary>
    private static string HastaCien(int numero)
    {
        if (numero < 30)
        {
            return Unidades[numero];
        }

        var unidad = numero % 10;
        return unidad == 0 ? Decenas[numero / 10] : $"{Decenas[numero / 10]} Y {Unidades[unidad]}";
    }

    /// <summary>"UNO" se acorta antes de MIL y MILLONES: VEINTIÚN MIL, TREINTA Y UN MILLONES.</summary>
    private static string Apocopar(string texto) =>
        texto.EndsWith("VEINTIUNO", StringComparison.Ordinal) ? texto[..^"VEINTIUNO".Length] + "VEINTIÚN"
        : texto.EndsWith("UNO", StringComparison.Ordinal) ? texto[..^"UNO".Length] + "UN"
        : texto;
}
