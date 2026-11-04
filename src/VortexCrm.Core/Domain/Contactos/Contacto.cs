namespace VortexCrm.Core.Domain.Contactos;

/// <summary>
/// Una persona con la que se mantiene una relación comercial: normalmente alguien que trabaja en un cliente (empresa).
/// Es un dato propio del CRM; el cliente es del hub y se referencia por su Id.
/// </summary>
public sealed class Contacto
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>DNI de la persona. Es opcional: muchos contactos se registran solo con nombre y teléfono.</summary>
    public string? Dni { get; set; }

    public string Nombres { get; set; } = "";

    public string Apellidos { get; set; } = "";

    public string? Telefono { get; set; }

    public string? Email { get; set; }

    public string? Cargo { get; set; }

    /// <summary>Cliente (empresa del hub) al que pertenece el contacto, si corresponde.</summary>
    public Guid? ClienteId { get; set; }

    public DateTimeOffset CreadoEn { get; init; } = DateTimeOffset.UtcNow;

    public string NombreCompleto => $"{Nombres} {Apellidos}".Trim();

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();

        if (!string.IsNullOrWhiteSpace(Dni) && !DocumentoIdentidad.EsDniValido(Dni))
        {
            errores.Add("El DNI debe tener 8 dígitos.");
        }

        if (string.IsNullOrWhiteSpace(Nombres))
        {
            errores.Add("Los nombres son obligatorios.");
        }

        return errores;
    }

    public Contacto Clonar() => (Contacto)MemberwiseClone();
}
