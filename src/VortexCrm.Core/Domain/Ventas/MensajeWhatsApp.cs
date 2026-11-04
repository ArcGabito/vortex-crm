using System.Text;
using VortexCrm.Core.Domain.Comun;

namespace VortexCrm.Core.Domain.Ventas;

/// <summary>Arma el mensaje y el enlace de WhatsApp para enviar una cotización.</summary>
public static class MensajeWhatsApp
{
    /// <summary>Más líneas que esto se resumen, para que el mensaje no sea interminable.</summary>
    public const int MaximoLineas = 10;

    public static string Cotizacion(Cotizacion cotizacion, string negocio, string? saludo)
    {
        var texto = new StringBuilder();
        texto.AppendLine(string.IsNullOrWhiteSpace(saludo) ? "Hola," : $"Hola {saludo},");
        texto.AppendLine($"te envío la cotización {cotizacion.Codigo} de {negocio}:");
        texto.AppendLine();

        foreach (var linea in cotizacion.Lineas.Take(MaximoLineas))
        {
            var cantidad = linea.Cantidad.ToString("0.##", FormatoPeru.Cultura);
            texto.AppendLine($"• {cantidad} × {linea.Descripcion}: {FormatoPeru.Soles(linea.Importe)}");
        }

        if (cotizacion.Lineas.Count > MaximoLineas)
        {
            texto.AppendLine($"• … y {cotizacion.Lineas.Count - MaximoLineas} más");
        }

        texto.AppendLine();
        var nota = cotizacion.ModoIgv == ModoIgv.NoAplica ? "" : " (incluye IGV)";
        texto.AppendLine($"*Total: {FormatoPeru.Soles(cotizacion.Totales.Total)}*{nota}");
        texto.Append($"Válida hasta el {cotizacion.ValidaHasta.ToString("dd/MM/yyyy", FormatoPeru.Cultura)}.");

        return texto.ToString();
    }

    /// <summary>Enlace wa.me que abre el chat con el mensaje escrito; <c>null</c> si el teléfono no es un celular.</summary>
    public static string? Enlace(string? telefono, string mensaje) =>
        TelefonoPeru.ParaWhatsApp(telefono) is { } numero
            ? $"https://wa.me/{numero}?text={Uri.EscapeDataString(mensaje)}"
            : null;
}
