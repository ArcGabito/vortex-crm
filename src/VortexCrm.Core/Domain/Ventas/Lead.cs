using VortexCrm.Core.Domain.Contactos;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>
/// Persona o empresa interesada que todavía no es cliente. Se califica y, al convertirla, pasa a ser cliente (o se une al
/// que ya existe con su documento), con su contacto y, si se quiere, una oportunidad.
/// </summary>
public sealed class Lead
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Nombre de la persona.</summary>
    public string Nombre { get; set; } = "";

    /// <summary>Empresa, si el interesado viene por una.</summary>
    public string? Empresa { get; set; }

    /// <summary><c>RUC</c> o <c>DNI</c> (opcional).</summary>
    public string? TipoDocumento { get; set; }

    public string? Documento { get; set; }

    public string? Telefono { get; set; }

    public string? Email { get; set; }

    /// <summary>De dónde vino: ver <see cref="OrigenesLead"/>.</summary>
    public string Origen { get; set; } = OrigenesLead.Web;

    public string Estado { get; internal set; } = EstadosLead.Nuevo;

    public string? Notas { get; set; }

    /// <summary>Por qué se descartó (solo en <see cref="EstadosLead.Descartado"/>).</summary>
    public string? MotivoDescarte { get; internal set; }

    /// <summary>Vendedor responsable: un vendedor solo edita los suyos.</summary>
    public Guid? ResponsableId { get; set; }

    public string? ResponsableNombre { get; set; }

    /// <summary>Lo que se creó al convertirlo.</summary>
    public Guid? ClienteId { get; internal set; }

    public Guid? ContactoId { get; internal set; }

    public Guid? OportunidadId { get; internal set; }

    public DateTimeOffset CreadoEn { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ConvertidoEn { get; internal set; }

    public bool EstaAbierto => Estado is EstadosLead.Nuevo or EstadosLead.Contactado or EstadosLead.Calificado;

    /// <summary>El nombre con el que se ve: la empresa si la hay, si no la persona.</summary>
    public string Titulo => string.IsNullOrWhiteSpace(Empresa) ? Nombre.Trim() : Empresa.Trim();

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Nombre))
            errores.Add(T("error.el_nombre_es_obligatorio"));
        if (!OrigenesLead.Todos.Contains(Origen))
            errores.Add(T("error.origen_del_lead_desconocido"));
        var tipo = TipoDocumento?.Trim().ToUpperInvariant();
        var doc = Documento?.Trim();
        if (string.IsNullOrEmpty(tipo) != string.IsNullOrEmpty(doc))
            errores.Add(T("error.el_tipo_el_numero_de"));
        else if (tipo == "RUC" && !DocumentoIdentidad.EsRucValido(doc!))
            errores.Add(T("error.el_ruc_debe_tener_11"));
        else if (tipo == "DNI" && !DocumentoIdentidad.EsDniValido(doc!))
            errores.Add(T("error.el_dni_debe_tener_8"));
        else if (tipo is not (null or "" or "RUC" or "DNI"))
            errores.Add(T("error.el_tipo_el_numero_de"));
        if (!string.IsNullOrWhiteSpace(Email) && !Email.Contains('@'))
            errores.Add(T("error.el_correo_no_es_valido"));
        if (string.IsNullOrWhiteSpace(Telefono) && string.IsNullOrWhiteSpace(Email))
            errores.Add(T("error.lead_sin_telefono_ni_correo"));
        return errores;
    }

    internal void CambiarEstado(string estado, string? motivo = null)
    {
        Estado = estado;
        MotivoDescarte = estado == EstadosLead.Descartado ? motivo?.Trim() : null;
    }

    internal void MarcarConvertido(Guid clienteId, Guid? contactoId, Guid? oportunidadId, DateTimeOffset ahora)
    {
        Estado = EstadosLead.Convertido;
        ClienteId = clienteId;
        ContactoId = contactoId;
        OportunidadId = oportunidadId;
        ConvertidoEn = ahora;
        MotivoDescarte = null;
    }

    public Lead Clonar() => (Lead)MemberwiseClone();

    /// <summary>Para leer lo guardado (incluye los campos que solo cambia el servicio).</summary>
    internal static Lead Cargar(Guid id, string nombre, string? empresa, string? tipoDocumento, string? documento, string? telefono,
        string? email, string origen, string estado, string? notas, string? motivoDescarte, Guid? responsableId, string? responsableNombre,
        Guid? clienteId, Guid? contactoId, Guid? oportunidadId, DateTimeOffset creadoEn, DateTimeOffset? convertidoEn) => new()
    {
        Id = id, Nombre = nombre, Empresa = empresa, TipoDocumento = tipoDocumento, Documento = documento, Telefono = telefono,
        Email = email, Origen = origen, Estado = estado, Notas = notas, MotivoDescarte = motivoDescarte, ResponsableId = responsableId,
        ResponsableNombre = responsableNombre, ClienteId = clienteId, ContactoId = contactoId, OportunidadId = oportunidadId,
        CreadoEn = creadoEn, ConvertidoEn = convertidoEn,
    };
}

/// <summary>Origen de un lead (código estable; el texto se traduce en la app).</summary>
public static class OrigenesLead
{
    public const string Web = "web";
    public const string Referido = "referido";
    public const string Redes = "redes";
    public const string Evento = "evento";
    public const string Llamada = "llamada";
    public const string Otro = "otro";

    public static readonly IReadOnlyList<string> Todos = [Web, Referido, Redes, Evento, Llamada, Otro];
}

public static class EstadosLead
{
    public const string Nuevo = "nuevo";
    public const string Contactado = "contactado";
    public const string Calificado = "calificado";
    public const string Descartado = "descartado";
    public const string Convertido = "convertido";

    public static readonly IReadOnlyList<string> Todos = [Nuevo, Contactado, Calificado, Descartado, Convertido];
}

/// <summary>Qué crear al convertir un lead. Sin <see cref="ClienteId"/> se usa el cliente con ese documento o se crea uno.</summary>
public sealed record ConversionLead(bool CrearOportunidad = true, string? TituloOportunidad = null, decimal Monto = 0,
    Guid? EmbudoId = null, Guid? ClienteId = null);

public sealed record ResultadoConversion(Guid ClienteId, bool ClienteNuevo, Guid ContactoId, Guid? OportunidadId);
