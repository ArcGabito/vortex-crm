using System.Text;
using VortexCrm.Core.Domain.Comun;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Arma el mensaje y el enlace de WhatsApp para enviar una cotización.</summary>
public static class MensajeWhatsApp
{
    /// <summary>Más líneas que esto se resumen, para que el mensaje no sea interminable.</summary>
    public const int MaximoLineas = 10;

    public static string Cotizacion(Cotizacion cotizacion, string negocio, string? saludo)
    {
        var texto = new StringBuilder();
        texto.AppendLine(string.IsNullOrWhiteSpace(saludo) ? T("whatsapp.hola") : T("whatsapp.hola_nombre", saludo));
        texto.AppendLine(T("whatsapp.te_envio", cotizacion.Codigo, negocio));
        texto.AppendLine();

        foreach (var linea in cotizacion.Lineas.Take(MaximoLineas))
        {
            var cantidad = linea.Cantidad.ToString("0.##", FormatoPeru.Cultura);
            texto.AppendLine($"• {cantidad} × {linea.Descripcion}: {Dinero(cotizacion, linea.Importe)}");
        }

        if (cotizacion.Lineas.Count > MaximoLineas)
        {
            texto.AppendLine(T("whatsapp.y_mas", cotizacion.Lineas.Count - MaximoLineas));
        }

        texto.AppendLine();
        var nota = cotizacion.ModoIgv == ModoIgv.NoAplica ? "" : T("whatsapp.incluye", cotizacion.NombreImpuesto);
        texto.AppendLine(T("whatsapp.total", Dinero(cotizacion, cotizacion.Totales.Total), nota));
        texto.Append(T("whatsapp.valida_hasta", cotizacion.ValidaHasta.ToString("dd/MM/yyyy", FormatoPeru.Cultura)));

        return texto.ToString();
    }

    /// <summary>Importe en la moneda de la cotización (<c>S/ 1,500.00</c>, <c>$ 1,500.00</c>…).</summary>
    private static string Dinero(Cotizacion cotizacion, decimal monto) =>
        VortexApi.Suite.Money.Currencies.Get(cotizacion.Moneda).Format(monto, FormatoPeru.Cultura);

    /// <summary>Enlace wa.me que abre el chat con el mensaje escrito; <c>null</c> si el teléfono no es un celular.</summary>
    public static string? Enlace(string? telefono, string mensaje) =>
        TelefonoPeru.ParaWhatsApp(telefono) is { } numero
            ? $"https://wa.me/{numero}?text={Uri.EscapeDataString(mensaje)}"
            : null;
}
