namespace VortexCrm.Core.Domain.Comun;

public static class TelefonoPeru
{
    private const string CodigoPais = "51";

    /// <summary>
    /// Convierte un teléfono al formato que usa WhatsApp (<c>51987654321</c>). Acepta
    /// celulares peruanos con o sin +51 y números internacionales que empiezan con +.
    /// Devuelve <c>null</c> si no parece un celular (por ejemplo, un fijo de Lima).
    /// </summary>
    public static string? ParaWhatsApp(string? telefono)
    {
        if (string.IsNullOrWhiteSpace(telefono))
        {
            return null;
        }

        var digitos = new string(telefono.Where(char.IsAsciiDigit).ToArray());

        // Celular peruano: 9 dígitos que empiezan con 9
        if (digitos.Length == 9 && digitos[0] == '9')
        {
            return CodigoPais + digitos;
        }

        if (digitos.Length == 11 && digitos.StartsWith(CodigoPais + "9", StringComparison.Ordinal))
        {
            return digitos;
        }

        // Número extranjero escrito con + y código de país
        if (telefono.TrimStart().StartsWith('+') && digitos.Length is >= 10 and <= 15 && !digitos.StartsWith(CodigoPais, StringComparison.Ordinal))
        {
            return digitos;
        }

        return null;
    }
}
