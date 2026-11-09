using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Equipos;

/// <summary>
/// Equipo de venta (CRM-53): un líder, sus vendedores y su territorio (ciudades, regiones o distritos de los clientes). Un
/// equipo puede estar dentro de otro (<see cref="PadreId"/>): el líder del de arriba ve también lo de los de abajo.
/// </summary>
public sealed class EquipoVenta
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Nombre { get; set; } = "";
    public Guid? LiderId { get; set; }
    public string? LiderNombre { get; set; }
    public List<Vendedor> Miembros { get; set; } = [];

    /// <summary>Zonas del territorio: se comparan, sin mayúsculas ni tildes, con la ciudad, región o distrito del cliente.</summary>
    public List<string> Zonas { get; set; } = [];

    public Guid? PadreId { get; set; }

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(Nombre))
            errores.Add(T("error.equipo_sin_nombre"));
        if (LiderId is null && Miembros.Count == 0)
            errores.Add(T("error.equipo_sin_personas"));
        if (PadreId == Id)
            errores.Add(T("error.equipo_padre"));
        return errores;
    }

    /// <summary>Si el cliente cae en el territorio del equipo.</summary>
    public bool Cubre(PartyRow cliente) =>
        Zonas.Count > 0 && new[] { cliente.City, cliente.Region, cliente.District }.Any(z => z is not null && Zonas.Any(t => Igual(t, z)));

    internal static bool Igual(string a, string b) =>
        string.Compare(a.Trim(), b.Trim(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) == 0;
}

/// <summary>Quién ve qué: todo el equipo comercial ve todo (como hasta ahora) o cada uno lo suyo y lo de sus equipos.</summary>
public enum Visibilidad
{
    Todo,
    Jerarquia,
}

public static class BasesComision
{
    /// <summary>Sobre el monto de las oportunidades ganadas en el mes.</summary>
    public const string Ganado = "ganado";

    /// <summary>Sobre los cobros del mes (los que informa el ERP o el POS) de los clientes de su cartera.</summary>
    public const string Cobrado = "cobrado";
}

/// <summary>
/// Plan de comisiones (CRM-54): un porcentaje sobre lo ganado o lo cobrado; con <see cref="TasaSobreMeta"/>, lo que pasa la
/// meta del mes se paga a esa tasa (acelerador). Un vendedor puede tener su propia tasa.
/// </summary>
public sealed record PlanComisiones(string Base, decimal Tasa, decimal? TasaSobreMeta = null, IReadOnlyDictionary<Guid, decimal>? TasaPorVendedor = null)
{
    public static readonly PlanComisiones Ninguno = new(BasesComision.Ganado, 0);

    public decimal TasaDe(Guid vendedor) => TasaPorVendedor is not null && TasaPorVendedor.TryGetValue(vendedor, out var t) ? t : Tasa;

    /// <summary>Comisión de un monto con su meta: la tasa hasta la meta y, con acelerador, la tasa sobre meta en el exceso.</summary>
    public decimal Calcular(Guid vendedor, decimal monto, decimal? meta)
    {
        var tasa = TasaDe(vendedor);
        if (TasaSobreMeta is not { } sobre || meta is not > 0 || monto <= meta)
            return Math.Round(monto * tasa / 100, 2, MidpointRounding.AwayFromZero);
        return Math.Round((meta.Value * tasa + (monto - meta.Value) * sobre) / 100, 2, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Comisión de un vendedor en un mes.</summary>
public sealed record ComisionVendedor(Guid VendedorId, string Nombre, decimal Base, decimal? Meta, decimal Tasa, decimal Comision, int Operaciones)
{
    public decimal? Avance => Meta is > 0 ? Math.Round(Base * 100 / Meta.Value, 1) : null;
}
