namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Cómo se aplica el IGV a los precios de una cotización.</summary>
public enum ModoIgv
{
    /// <summary>Los precios ya incluyen el IGV; se desglosa del total (lo usual al vender a personas).</summary>
    Incluido,

    /// <summary>Los precios son sin IGV; el 18 % se suma al final (lo usual entre empresas).</summary>
    Adicional,

    /// <summary>No se cobra IGV: Nuevo RUS, exonerados o inafectos.</summary>
    NoAplica,
}

public static class Impuestos
{
    /// <summary>Tasa general: 16 % de IGV + 2 % de Impuesto de Promoción Municipal.</summary>
    public const decimal TasaIgv = 0.18m;

    public static string Nombre(this ModoIgv modo) => modo switch
    {
        ModoIgv.Incluido => "Precios con IGV incluido",
        ModoIgv.Adicional => "Precios sin IGV (se suma 18 %)",
        _ => "Sin IGV (Nuevo RUS, exonerado)",
    };
}
