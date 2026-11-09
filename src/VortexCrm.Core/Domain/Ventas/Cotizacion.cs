using VortexApi.Suite.Money;
using VortexApi.Suite.Taxes;
using VortexCrm.Core.Domain.Comun;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Ventas;

public enum EstadoCotizacion
{
    Borrador,
    Enviada,
    Aceptada,
    Rechazada,

    /// <summary>Hay una versión más nueva de esta cotización.</summary>
    Reemplazada,
}

public static class EstadosCotizacion
{
    public static IReadOnlyList<EstadoCotizacion> Todos { get; } = Enum.GetValues<EstadoCotizacion>();
}

/// <summary>Una línea de la cotización: producto o servicio, cantidad y precio.</summary>
public sealed class LineaCotizacion
{
    /// <summary>Artículo del catálogo del hub del que salió la línea. Solo las líneas con artículo pasan al pedido.</summary>
    public Guid? ItemId { get; set; }

    public string Descripcion { get; set; } = "";

    /// <summary>Unidad de medida: UND, KG, M, SERVICIO, etc.</summary>
    public string Unidad { get; set; } = "UND";

    public decimal Cantidad { get; set; } = 1;

    /// <summary>Precio por unidad, con o sin IGV según el <see cref="ModoIgv"/> de la cotización.</summary>
    public decimal PrecioUnitario { get; set; }

    public decimal Importe => Math.Round(Cantidad * PrecioUnitario, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Precio de lista al cotizar (de la lista del cliente o de la sede, o el del artículo), en el modo de impuesto de la
    /// cotización. Contra él se mide el descuento.
    /// </summary>
    public decimal? PrecioLista { get; set; }

    /// <summary>Producto opcional: se ofrece aparte, no suma al total y no pasa al pedido.</summary>
    public bool Opcional { get; set; }

    /// <summary>Descuento sobre el precio de lista, en porcentaje (0 si no tiene precio de lista o no baja).</summary>
    public decimal Descuento => PrecioLista is > 0 and var lista && PrecioUnitario < lista
        ? Math.Round((lista - PrecioUnitario) / lista * 100, 2, MidpointRounding.AwayFromZero)
        : 0;

    /// <summary>Una línea recién agregada que todavía no tiene nada escrito.</summary>
    public bool EstaVacia => string.IsNullOrWhiteSpace(Descripcion) && PrecioUnitario == 0 && ItemId is null;

    public LineaCotizacion Clonar() => (LineaCotizacion)MemberwiseClone();
}

/// <summary>Totales de la cotización. Sin impuesto, el subtotal es igual al total.</summary>
public sealed record TotalesCotizacion(decimal Subtotal, decimal Igv, decimal Total)
{
    /// <param name="tasa">Tasa general del país (0.18 en Perú, 0.16 en México…).</param>
    /// <param name="decimales">Decimales de la moneda (0 en pesos chilenos).</param>
    public static TotalesCotizacion Calcular(IEnumerable<LineaCotizacion> lineas, ModoIgv modo, decimal tasa = Impuestos.TasaIgv, int decimales = 2)
    {
        decimal Redondear(decimal monto) => Math.Round(monto, decimales, MidpointRounding.AwayFromZero);
        var suma = Redondear(lineas.Sum(l => l.Importe));

        switch (modo)
        {
            case ModoIgv.Incluido:
                var valorVenta = Redondear(suma / (1 + tasa));
                return new TotalesCotizacion(valorVenta, suma - valorVenta, suma);

            case ModoIgv.Adicional:
                var igv = Redondear(suma * tasa);
                return new TotalesCotizacion(suma, igv, suma + igv);

            default:
                return new TotalesCotizacion(suma, 0, suma);
        }
    }

}

public sealed class Cotizacion
{
    public const int DiasDeValidezPredeterminados = 15;

    public Guid Id { get; internal set; } = Guid.NewGuid();

    /// <summary>Número por equipo (<c>COT-3F0C-0012</c>), asignado al guardarla por primera vez.</summary>
    public string? Numero { get; internal set; }

    /// <summary>Número con la versión (COT-…-000004 v2) desde la segunda.</summary>
    public string Codigo => Numero is null ? "Nueva" : Version > 1 ? $"{Numero} v{Version}" : Numero;

    /// <summary>Cliente (empresa o persona del hub).</summary>
    public Guid? ClienteId { get; set; }

    public Guid? ContactoId { get; set; }

    /// <summary>Oportunidad del pipeline a la que corresponde, si la hay.</summary>
    public Guid? OportunidadId { get; set; }

    /// <summary>Sede del hub que la emite (su dirección va en el PDF); <c>null</c> es el domicilio fiscal.</summary>
    public Guid? SedeId { get; set; }

    /// <summary>Pedido generado desde esta cotización (Id determinista, el mismo que calcula el ERP).</summary>
    public Guid? PedidoId { get; internal set; }

    public DateOnly FechaEmision { get; set; } = FormatoPeru.Hoy();

    public DateOnly ValidaHasta { get; set; } = FormatoPeru.Hoy().AddDays(DiasDeValidezPredeterminados);

    public ModoIgv ModoIgv { get; set; } = ModoIgv.Incluido;

    public List<LineaCotizacion> Lineas { get; internal set; } = [];

    /// <summary>Forma de pago, tiempo de entrega, garantía, etc.</summary>
    public string? Condiciones { get; set; }

    public EstadoCotizacion Estado { get; internal set; } = EstadoCotizacion.Borrador;

    public DateTimeOffset CreadoEn { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>Moneda de la cotización (ISO 4217); por defecto la de la empresa.</summary>
    public string Moneda { get; set; } = "PEN";

    /// <summary>
    /// Impuesto del país de la empresa (nombre y tasa). No se guarda: lo pone el servicio al cargar la cotización, así una
    /// empresa de México cotiza con IVA 16 % y una de Perú con IGV 18 %.
    /// </summary>
    public TaxRegime Regimen { get; set; } = TaxCatalog.For("PE");

    public decimal TasaImpuesto => Regimen.StandardRate;

    public string NombreImpuesto => Regimen.TaxName;

    /// <summary>Versión (1, 2, 3…): una nueva versión copia la anterior y la deja como Reemplazada.</summary>
    public int Version { get; set; } = 1;

    /// <summary>La primera versión (todas las versiones de una cotización apuntan a ella).</summary>
    public Guid? VersionDe { get; set; }

    /// <summary>Quién aprobó el descuento y hasta cuánto (si después se baja más el precio, hay que volver a aprobar).</summary>
    public string? AprobadaPor { get; set; }
    public DateTimeOffset? AprobadaEn { get; set; }
    public decimal? DescuentoAprobado { get; set; }

    /// <summary>El mayor descuento sobre el precio de lista entre las líneas que suman.</summary>
    public decimal DescuentoMaximo => Lineas.Where(l => !l.Opcional).Select(l => l.Descuento).DefaultIfEmpty(0).Max();

    /// <summary>Si con ese tope (null: sin tope) necesita la aprobación del gerente para enviarse.</summary>
    public bool RequiereAprobacion(decimal? tope) => tope is { } t && DescuentoMaximo > t && (DescuentoAprobado ?? -1) < DescuentoMaximo;

    /// <summary>Vendedor responsable (usuario del hub): un vendedor solo edita las suyas.</summary>
    public Guid? ResponsableId { get; set; }

    public string? ResponsableNombre { get; set; }

    /// <summary>Totales de lo que se compra (las líneas opcionales no suman).</summary>
    public TotalesCotizacion Totales => TotalesCotizacion.Calcular(Lineas.Where(l => !l.Opcional), ModoIgv, TasaImpuesto, Currencies.Get(Moneda).Decimals);

    /// <summary>Lo que sumarían las opcionales si el cliente las toma.</summary>
    public decimal TotalOpcionales => TotalesCotizacion.Calcular(Lineas.Where(l => l.Opcional), ModoIgv, TasaImpuesto, Currencies.Get(Moneda).Decimals).Total;

    /// <summary>Agrega la línea; si hay una línea vacía (como la que trae una cotización nueva), la reemplaza.</summary>
    public void AgregarLinea(LineaCotizacion linea)
    {
        var vacia = Lineas.FindIndex(l => l.EstaVacia);
        if (vacia >= 0)
        {
            Lineas[vacia] = linea;
        }
        else
        {
            Lineas.Add(linea);
        }
    }

    /// <summary>
    /// Cambia el estado y avanza la oportunidad vinculada: al enviarse pasa de Prospecto a
    /// Cotizado, y al aceptarse queda Ganada. Si la oportunidad no tenía monto, toma el
    /// total de la cotización. Devuelve <c>true</c> si la oportunidad cambió y hay que guardarla.
    /// </summary>
    public bool CambiarEstado(EstadoCotizacion nuevo, Oportunidad? oportunidad, DateTimeOffset ahora)
    {
        if (oportunidad is not null && oportunidad.Id != OportunidadId)
        {
            throw new ArgumentException("La oportunidad no corresponde a esta cotización.", nameof(oportunidad));
        }

        Estado = nuevo;

        if (oportunidad is null || nuevo is EstadoCotizacion.Borrador or EstadoCotizacion.Rechazada)
        {
            return false;
        }

        var etapaAnterior = oportunidad.Etapa;
        var montoAnterior = oportunidad.Monto;

        if (nuevo == EstadoCotizacion.Enviada && oportunidad.Etapa == EtapaOportunidad.Prospecto)
        {
            oportunidad.CambiarEtapa(EtapaOportunidad.Cotizado, ahora);
        }
        else if (nuevo == EstadoCotizacion.Aceptada && oportunidad.Etapa.EstaAbierta())
        {
            oportunidad.CambiarEtapa(EtapaOportunidad.Ganado, ahora);
        }

        if (oportunidad.Monto == 0)
        {
            oportunidad.Monto = Totales.Total;
        }

        return oportunidad.Etapa != etapaAnterior || oportunidad.Monto != montoAnterior;
    }

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();

        if (ClienteId is null)
        {
            errores.Add(T("error.elige_el_cliente"));
        }

        if (Lineas.Count == 0)
        {
            errores.Add(T("error.agrega_al_menos_un_producto"));
        }

        for (var i = 0; i < Lineas.Count; i++)
        {
            var linea = Lineas[i];
            if (string.IsNullOrWhiteSpace(linea.Descripcion))
            {
                errores.Add(T("error.linea_falta_la_descripcion", (i + 1)));
            }

            if (linea.Cantidad <= 0)
            {
                errores.Add(T("error.linea_la_cantidad_debe_ser", (i + 1)));
            }

            if (linea.PrecioUnitario < 0)
            {
                errores.Add(T("error.linea_el_precio_no_puede", (i + 1)));
            }
        }

        if (ValidaHasta < FechaEmision)
        {
            errores.Add(T("error.la_fecha_de_validez_no"));
        }

        return errores;
    }

    public Cotizacion Clonar()
    {
        var copia = (Cotizacion)MemberwiseClone();
        copia.Lineas = Lineas.Select(l => l.Clonar()).ToList();
        return copia;
    }
}
