using System.Text;

namespace VortexCrm.Core.Domain.Actividades;

public enum TipoInteraccion
{
    Llamada,
    WhatsApp,
    Correo,
    Reunion,
    Visita,
    Nota,
}

/// <summary>
/// Lo que pasó con un cliente: una llamada, un WhatsApp, un correo, una reunión o una visita, con su resultado. Es un
/// registro del CRM (<c>crm.record.upserted</c>, kind <c>interaccion</c>) y se ve en la ficha del cliente.
/// </summary>
public sealed class Interaccion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ClienteId { get; set; }
    public Guid? ContactoId { get; set; }
    public Guid? OportunidadId { get; set; }

    /// <summary>La tarea que se completó con esta interacción (si vino de una).</summary>
    public Guid? TareaId { get; set; }

    public TipoInteraccion Tipo { get; set; } = TipoInteraccion.Llamada;
    public DateTimeOffset Fecha { get; set; }
    public string Resumen { get; set; } = "";
    public string? Resultado { get; set; }
    public Guid? ResponsableId { get; set; }
    public string? ResponsableNombre { get; set; }
    public DateTimeOffset CreadoEn { get; set; }

    public Interaccion Clonar() => (Interaccion)MemberwiseClone();
}

public sealed record InteraccionVista(Interaccion Interaccion, string? Cliente, string? Contacto);

public enum CanalPlantilla
{
    WhatsApp,
    Correo,
}

/// <summary>
/// Plantilla de mensaje para WhatsApp o correo, con variables entre llaves (<c>{cliente}</c>, <c>{contacto}</c>…). Es un
/// registro del CRM (kind <c>plantilla</c>): la comparte todo el equipo.
/// </summary>
public sealed class Plantilla
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nombre { get; set; } = "";
    public CanalPlantilla Canal { get; set; } = CanalPlantilla.WhatsApp;
    public string? Asunto { get; set; }
    public string Cuerpo { get; set; } = "";

    public Plantilla Clonar() => (Plantilla)MemberwiseClone();
}

/// <summary>Valores para llenar una plantilla.</summary>
public sealed record DatosPlantilla(string? Cliente = null, string? Contacto = null, string? Vendedor = null, string? Empresa = null,
    string? Cotizacion = null, string? Monto = null, string? Fecha = null);

public static class Plantillas
{
    /// <summary>Variables que se pueden usar, en el orden en que se ofrecen.</summary>
    public static readonly string[] Variables = ["cliente", "contacto", "vendedor", "empresa", "cotizacion", "monto", "fecha"];

    /// <summary>Reemplaza las variables; las que no tienen valor quedan vacías (y se limpian los espacios dobles).</summary>
    public static string Llenar(string texto, DatosPlantilla datos)
    {
        var valores = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cliente"] = datos.Cliente, ["contacto"] = datos.Contacto, ["vendedor"] = datos.Vendedor, ["empresa"] = datos.Empresa,
            ["cotizacion"] = datos.Cotizacion, ["monto"] = datos.Monto, ["fecha"] = datos.Fecha,
        };
        var salida = new StringBuilder(texto.Length);
        for (var i = 0; i < texto.Length; i++)
        {
            if (texto[i] == '{' && texto.IndexOf('}', i) is var fin and > 0 && valores.TryGetValue(texto[(i + 1)..fin], out var valor))
            {
                salida.Append(valor);
                i = fin;
            }
            else
                salida.Append(texto[i]);
        }
        return string.Join('\n', salida.ToString().Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, " {2,}", " ").TrimEnd()));
    }
}
