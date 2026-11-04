using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Los datos del cliente tal como se muestran en la cotización.</summary>
public sealed record ClienteCotizacion(
    string Nombre,
    string? Documento,
    string? Direccion,
    string? Atencion,
    string? Telefono,
    string? Email)
{
    /// <summary>
    /// Arma los datos del cliente: la cotización va a su nombre o razón social y su documento, "a la atención de" la
    /// persona de contacto si la hay (cuyo teléfono y correo tienen prioridad).
    /// </summary>
    public static ClienteCotizacion Crear(PartyRow cliente, Contacto? contacto) => new(
        cliente.Name,
        cliente.DocumentNumber is null ? null : $"{cliente.DocumentType} {cliente.DocumentNumber}".Trim(),
        cliente.FullAddress,
        contacto?.NombreCompleto,
        contacto?.Telefono ?? cliente.Phone,
        contacto?.Email ?? cliente.Email);
}
