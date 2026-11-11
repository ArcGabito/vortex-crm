using System.Globalization;

namespace VortexCrm.Core.Domain.Segmentos;

/// <summary>A qué se le pueden agregar campos personalizados (CRM-06).</summary>
public static class EntidadesCampo
{
    public const string Cliente = "cliente";
    public const string Oportunidad = "oportunidad";
    public const string Cotizacion = "cotizacion";

    public static readonly string[] Todas = [Cliente, Oportunidad, Cotizacion];
}

public static class TiposCampo
{
    public const string Texto = "texto";
    public const string Numero = "numero";
    public const string Fecha = "fecha";

    /// <summary>Una opción de una lista cerrada (<see cref="CampoPersonalizado.Opciones"/>).</summary>
    public const string Lista = "lista";
    public const string SiNo = "si_no";

    public static readonly string[] Todos = [Texto, Numero, Fecha, Lista, SiNo];
}

/// <summary>
/// Campo que el gerente agrega a clientes, oportunidades o cotizaciones (CRM-06). Los valores se guardan como texto
/// normalizado: números con punto, fechas <c>yyyy-MM-dd</c>, sí/no como <c>true</c>/<c>false</c>.
/// </summary>
public sealed record CampoPersonalizado(Guid Id, string Entidad, string Nombre, string Tipo, IReadOnlyList<string>? Opciones = null,
    bool Obligatorio = false, int Orden = 0, bool Activo = true)
{
    public const int MaxTexto = 500;

    /// <summary>El valor normalizado, null si quedó vacío, o el mensaje de error (<c>Error</c>) si no sirve para el campo.</summary>
    public (string? Valor, string? Error) Normalizar(string? valor)
    {
        var v = valor?.Trim();
        if (string.IsNullOrEmpty(v))
            return (null, Obligatorio ? "obligatorio" : null);
        switch (Tipo)
        {
            case TiposCampo.Numero:
                var texto = v.Replace(" ", "");
                if (texto.Contains(',') && !texto.Contains('.'))
                    texto = texto.Replace(',', '.');
                return decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                    ? (n.ToString(CultureInfo.InvariantCulture), null) : (null, "numero");
            case TiposCampo.Fecha:
                return DateOnly.TryParse(v, CultureInfo.InvariantCulture, out var d) || DateOnly.TryParse(v, CultureInfo.GetCultureInfo("es-PE"), out d)
                    ? (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null) : (null, "fecha");
            case TiposCampo.Lista:
                var opcion = Opciones?.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
                return opcion is null ? (null, "lista") : (opcion, null);
            case TiposCampo.SiNo:
                return v.ToLowerInvariant() switch
                {
                    "true" or "si" or "sí" or "1" or "yes" => ("true", null),
                    "false" or "no" or "0" => ("false", null),
                    _ => (null, "si_no"),
                };
            default:
                return v.Length > MaxTexto ? (null, "texto") : (v, null);
        }
    }
}

/// <summary>Etiquetas del cliente y su matriz (CRM-05, CRM-07). Datos del CRM: no van al hub.</summary>
public sealed record PerfilCliente(Guid ClienteId, IReadOnlyList<string> Etiquetas, Guid? MatrizId);

/// <summary>Etiquetas en minúsculas, sin espacios repetidos, hasta 40 caracteres y sin repetir.</summary>
public static class Etiquetas
{
    public const int Max = 20;
    public const int Largo = 40;

    public static List<string> Normalizar(IEnumerable<string>? etiquetas) =>
        (etiquetas ?? []).Select(e => string.Join(' ', e.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant())
            .Where(e => e.Length > 0).Select(e => e.Length > Largo ? e[..Largo] : e).Distinct().Take(Max).ToList();
}

/// <summary>
/// Criterios de un segmento de clientes (CRM-05). Todos opcionales; se combinan con "y".
/// </summary>
/// <param name="TodasLasEtiquetas">Con varias etiquetas: true exige todas; false, cualquiera.</param>
/// <param name="GrupoDe">La empresa matriz y sus sucursales.</param>
/// <param name="SinActividadDias">Sin compras, comprobantes, cobros ni interacciones en esos días.</param>
public sealed record FiltroClientes(
    string? Busqueda = null, IReadOnlyList<string>? Etiquetas = null, bool TodasLasEtiquetas = false, string? Ciudad = null,
    Guid? ResponsableId = null, bool SinResponsable = false, Guid? GrupoDe = null, Guid? CampoId = null, string? CampoValor = null,
    int? SinActividadDias = null);

/// <summary>Segmento guardado: un nombre para un filtro. Privado de quien lo creó o compartido con todos.</summary>
public sealed record ListaGuardada(Guid Id, string Nombre, FiltroClientes Filtro, bool Compartida, Guid? CreadaPorId, string? CreadaPor);

public sealed record UsoEtiqueta(string Etiqueta, int Clientes);

/// <summary>Cliente de un segmento con sus etiquetas y su matriz.</summary>
public sealed record ClienteSegmento(PartyRow Cliente, IReadOnlyList<string> Etiquetas, Guid? MatrizId, string? MatrizNombre);

/// <summary>Grupo empresarial de un cliente: su matriz (si es sucursal) y sus sucursales (si es matriz).</summary>
public sealed record GrupoEmpresarial(PartyRow? Matriz, IReadOnlyList<PartyRow> Sucursales);
