namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Cómo se aplica el impuesto al consumo (IGV, IVA, VAT…) a los precios de una cotización.</summary>
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
    /// <summary>Tasa general de Perú (16 % de IGV + 2 % de IPM). La de cada empresa sale de su país (<see cref="Cotizacion.Regimen"/>).</summary>
    public const decimal TasaIgv = 0.18m;

    /// <summary>"Precios con IGV incluido", "Precios sin IVA (se suma 16 %)"… según el impuesto del país.</summary>
    public static string Nombre(this ModoIgv modo, string impuesto = "IGV", decimal tasa = TasaIgv) => modo switch
    {
        ModoIgv.Incluido => $"Precios con {impuesto} incluido",
        ModoIgv.Adicional => $"Precios sin {impuesto} (se suma {tasa * 100:0.##} %)",
        _ => $"Sin {impuesto} (exonerado, inafecto o régimen simplificado)",
    };
}
