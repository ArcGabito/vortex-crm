namespace VortexCrm.Core.Domain.Contactos;

/// <summary>Validación de documentos de identidad peruanos (RUC y DNI).</summary>
public static class DocumentoIdentidad
{
    private static readonly int[] PesosRuc = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

    /// <summary>
    /// Prefijos válidos de RUC: 10 (persona natural), 15 y 17 (casos especiales de
    /// personas naturales), 16 (sociedades conyugales) y 20 (personas jurídicas).
    /// </summary>
    private static readonly string[] PrefijosRuc = ["10", "15", "16", "17", "20"];

    /// <summary>
    /// Valida un RUC: 11 dígitos, prefijo conocido y dígito verificador
    /// (módulo 11 con los pesos que usa SUNAT).
    /// </summary>
    public static bool EsRucValido(string? ruc)
    {
        if (ruc is not { Length: 11 } || !ruc.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!PrefijosRuc.Contains(ruc[..2]))
        {
            return false;
        }

        var suma = 0;
        for (var i = 0; i < PesosRuc.Length; i++)
        {
            suma += (ruc[i] - '0') * PesosRuc[i];
        }

        var verificador = 11 - (suma % 11);
        if (verificador == 10)
        {
            verificador = 0;
        }
        else if (verificador == 11)
        {
            verificador = 1;
        }

        return ruc[10] - '0' == verificador;
    }

    /// <summary>Valida el formato de un DNI: exactamente 8 dígitos.</summary>
    public static bool EsDniValido(string? dni) =>
        dni is { Length: 8 } && dni.All(char.IsAsciiDigit);

    /// <summary>Indica si el RUC es de una persona jurídica (empresa), es decir, si empieza con 20.</summary>
    public static bool EsRucDePersonaJuridica(string ruc) => ruc.StartsWith("20", StringComparison.Ordinal);
}
