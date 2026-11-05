using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Texts;
using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Pdf;

/// <summary>Quien emite la cotización: la empresa del hub, con la dirección de la sede que cotiza.</summary>
public sealed record EmisorPdf(string Nombre, string? RazonSocial, string? Ruc, string? Direccion, string? Telefono, string? Email);

/// <summary>Genera el PDF de una cotización (A4) con PDFsharp/MigraDoc, sin dependencias nativas.</summary>
public static class GeneradorPdfCotizaciones
{
    /// <summary>El naranja de la marca Vortex.</summary>
    private static readonly Color Marca = new(0xFF, 0x5B, 0x14);
    private static readonly Color GrisClaro = new(0xF3, 0xF4, 0xF6);
    private static readonly Color GrisTexto = new(0x6B, 0x72, 0x80);

    public static byte[] Generar(Cotizacion cotizacion, EmisorPdf negocio, ClienteCotizacion cliente)
    {
        ResolvedorFuentes.Registrar();

        var documento = new Document();
        documento.Info.Title = T("pdf.cotizacion_n", cotizacion.Codigo);
        documento.Info.Author = negocio.Nombre;

        var normal = documento.Styles[StyleNames.Normal]!;
        normal.Font.Name = ResolvedorFuentes.Familia;
        normal.Font.Size = 9;

        var seccion = documento.AddSection();
        seccion.PageSetup = documento.DefaultPageSetup.Clone();
        seccion.PageSetup.PageFormat = PageFormat.A4;
        seccion.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        seccion.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        seccion.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
        seccion.PageSetup.RightMargin = Unit.FromCentimeter(1.8);

        AgregarEncabezado(seccion, cotizacion, negocio);
        AgregarCliente(seccion, cliente);
        AgregarLineas(seccion, cotizacion);
        AgregarTotales(seccion, cotizacion);
        AgregarCondiciones(seccion, cotizacion);

        var pie = seccion.Footers.Primary.AddParagraph(T("pdf.pie"));
        pie.Format.Alignment = ParagraphAlignment.Center;
        pie.Format.Font.Size = 7;
        pie.Format.Font.Color = GrisTexto;

        var renderizador = new PdfDocumentRenderer { Document = documento };
        renderizador.RenderDocument();

        using var memoria = new MemoryStream();
        renderizador.PdfDocument.Save(memoria);
        return memoria.ToArray();
    }

    private static void AgregarEncabezado(Section seccion, Cotizacion cotizacion, EmisorPdf negocio)
    {
        var tabla = seccion.AddTable();
        tabla.AddColumn(Unit.FromCentimeter(11.4));
        tabla.AddColumn(Unit.FromCentimeter(6));
        var fila = tabla.AddRow();
        fila.VerticalAlignment = VerticalAlignment.Center;

        // Emisor
        var emisor = fila.Cells[0];
        var nombre = emisor.AddParagraph(negocio.Nombre);
        nombre.Format.Font.Size = 16;
        nombre.Format.Font.Bold = true;
        nombre.Format.Font.Color = Marca;

        if (!string.IsNullOrWhiteSpace(negocio.RazonSocial) && negocio.RazonSocial != negocio.Nombre)
        {
            emisor.AddParagraph(negocio.RazonSocial);
        }

        AgregarSiHay(emisor, string.IsNullOrWhiteSpace(negocio.Ruc) ? null : T("pdf.ruc", negocio.Ruc));
        AgregarSiHay(emisor, negocio.Direccion);
        AgregarSiHay(emisor, Unir(" · ", negocio.Telefono, negocio.Email));

        // Recuadro con el número
        var recuadro = fila.Cells[1];
        recuadro.Shading.Color = Marca;
        recuadro.Format.Alignment = ParagraphAlignment.Center;
        fila.TopPadding = Unit.FromCentimeter(0.3);
        fila.BottomPadding = Unit.FromCentimeter(0.3);

        var titulo = recuadro.AddParagraph(T("pdf.titulo"));
        titulo.Format.Font.Bold = true;
        titulo.Format.Font.Size = 13;
        titulo.Format.Font.Color = Colors.White;

        var numero = recuadro.AddParagraph(T("pdf.numero", cotizacion.Codigo));
        numero.Format.Font.Size = 11;
        numero.Format.Font.Color = Colors.White;

        var fechas = seccion.AddParagraph();
        fechas.Format.Alignment = ParagraphAlignment.Right;
        fechas.Format.SpaceBefore = Unit.FromCentimeter(0.3);
        fechas.AddFormattedText(T("pdf.fecha"), TextFormat.Bold);
        fechas.AddText(cotizacion.FechaEmision.ToString("dd/MM/yyyy", FormatoPeru.Cultura));
        fechas.AddText("     ");
        fechas.AddFormattedText(T("pdf.valida_hasta"), TextFormat.Bold);
        fechas.AddText(cotizacion.ValidaHasta.ToString("dd/MM/yyyy", FormatoPeru.Cultura));
    }

    private static void AgregarCliente(Section seccion, ClienteCotizacion cliente)
    {
        var tabla = seccion.AddTable();
        tabla.AddColumn(Unit.FromCentimeter(17.4));
        var filaCliente = tabla.AddRow();
        filaCliente.TopPadding = Unit.FromCentimeter(0.2);
        filaCliente.BottomPadding = Unit.FromCentimeter(0.2);
        var celda = filaCliente.Cells[0];
        celda.Shading.Color = GrisClaro;
        celda.Format.LeftIndent = Unit.FromCentimeter(0.2);

        var nombre = celda.AddParagraph();
        nombre.AddFormattedText(T("pdf.cliente"), TextFormat.Bold);
        nombre.AddText(cliente.Nombre);
        AgregarSiHay(celda, Unir(" · ", cliente.Documento, cliente.Direccion));

        if (cliente.Atencion is not null)
        {
            var atencion = celda.AddParagraph();
            atencion.AddFormattedText(T("pdf.atencion"), TextFormat.Bold);
            atencion.AddText(cliente.Atencion);
        }

        AgregarSiHay(celda, Unir(" · ", cliente.Telefono, cliente.Email));

        seccion.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.2);
    }

    private static void AgregarLineas(Section seccion, Cotizacion cotizacion)
    {
        var tabla = seccion.AddTable();
        tabla.Borders.Bottom.Width = 0.5;
        tabla.Borders.Bottom.Color = GrisClaro;

        double[] anchos = [0.8, 8.0, 1.6, 1.6, 2.6, 2.8];
        foreach (var ancho in anchos)
        {
            tabla.AddColumn(Unit.FromCentimeter(ancho));
        }

        var encabezado = tabla.AddRow();
        encabezado.HeadingFormat = true;
        encabezado.Shading.Color = Marca;
        encabezado.Format.Font.Bold = true;
        encabezado.Format.Font.Color = Colors.White;
        string[] titulos = ["N°", T("pdf.descripcion"), T("pdf.cant"), T("pdf.unid"), T("pdf.p_unit"), T("pdf.importe")];
        for (var i = 0; i < titulos.Length; i++)
        {
            encabezado.Cells[i].AddParagraph(titulos[i]);
        }

        for (var i = 0; i < cotizacion.Lineas.Count; i++)
        {
            var linea = cotizacion.Lineas[i];
            var fila = tabla.AddRow();
            fila.TopPadding = Unit.FromCentimeter(0.1);
            fila.BottomPadding = Unit.FromCentimeter(0.1);
            fila.Cells[0].AddParagraph((i + 1).ToString(FormatoPeru.Cultura));
            fila.Cells[1].AddParagraph(linea.Descripcion);
            fila.Cells[2].AddParagraph(linea.Cantidad.ToString("0.##", FormatoPeru.Cultura));
            fila.Cells[3].AddParagraph(linea.Unidad);
            fila.Cells[4].AddParagraph(Dinero(cotizacion, linea.PrecioUnitario));
            fila.Cells[5].AddParagraph(Dinero(cotizacion, linea.Importe));
        }

        foreach (var columna in new[] { 2, 4, 5 })
        {
            tabla.Columns[columna].Format.Alignment = ParagraphAlignment.Right;
        }

        var nota = cotizacion.ModoIgv switch
        {
            ModoIgv.Incluido => T("pdf.precios_incluyen", cotizacion.NombreImpuesto),
            ModoIgv.Adicional => T("pdf.precios_no_incluyen", cotizacion.NombreImpuesto),
            _ => T("pdf.no_afecta", cotizacion.NombreImpuesto),
        };
        var parrafo = seccion.AddParagraph(nota);
        parrafo.Format.Font.Size = 7.5;
        parrafo.Format.Font.Color = GrisTexto;
        parrafo.Format.SpaceBefore = Unit.FromCentimeter(0.1);
    }

    private static void AgregarTotales(Section seccion, Cotizacion cotizacion)
    {
        var totales = cotizacion.Totales;

        var tabla = seccion.AddTable();
        tabla.Rows.LeftIndent = Unit.FromCentimeter(10.4);
        tabla.AddColumn(Unit.FromCentimeter(4.2));
        tabla.AddColumn(Unit.FromCentimeter(2.8));
        tabla.Columns[1].Format.Alignment = ParagraphAlignment.Right;

        if (cotizacion.ModoIgv != ModoIgv.NoAplica)
        {
            AgregarFilaTotal(tabla, T("pdf.subtotal"), totales.Subtotal, cotizacion, destacada: false);
            AgregarFilaTotal(tabla, $"{cotizacion.NombreImpuesto} ({cotizacion.TasaImpuesto * 100:0.##} %)", totales.Igv, cotizacion, destacada: false);
        }

        AgregarFilaTotal(tabla, T("pdf.total"), totales.Total, cotizacion, destacada: true);

        // El monto en letras (costumbre de los comprobantes de LATAM) solo existe en español.
        if (CoreText.Language == VortexApi.Suite.Regional.Languages.Spanish)
        {
            var letras = seccion.AddParagraph();
            letras.Format.SpaceBefore = Unit.FromCentimeter(0.4);
            letras.AddFormattedText("SON: ", TextFormat.Bold);
            letras.AddText(MontoEnLetras.Monto(totales.Total, cotizacion.Moneda));
        }
    }

    private static void AgregarFilaTotal(Table tabla, string concepto, decimal monto, Cotizacion cotizacion, bool destacada)
    {
        var fila = tabla.AddRow();
        fila.TopPadding = Unit.FromCentimeter(0.1);
        fila.BottomPadding = Unit.FromCentimeter(0.1);
        fila.Cells[0].AddParagraph(concepto);
        fila.Cells[1].AddParagraph(Dinero(cotizacion, monto));

        if (destacada)
        {
            fila.Shading.Color = Marca;
            fila.Format.Font.Bold = true;
            fila.Format.Font.Size = 10;
            fila.Format.Font.Color = Colors.White;
        }
    }

    private static void AgregarCondiciones(Section seccion, Cotizacion cotizacion)
    {
        if (string.IsNullOrWhiteSpace(cotizacion.Condiciones))
        {
            return;
        }

        var titulo = seccion.AddParagraph(T("pdf.condiciones"));
        titulo.Format.Font.Bold = true;
        titulo.Format.SpaceBefore = Unit.FromCentimeter(0.6);

        var texto = seccion.AddParagraph();
        var lineas = cotizacion.Condiciones.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lineas.Length; i++)
        {
            if (i > 0)
            {
                texto.AddLineBreak();
            }

            texto.AddText(lineas[i]);
        }
    }

    private static string Dinero(Cotizacion cotizacion, decimal monto) =>
        VortexApi.Suite.Money.Currencies.Get(cotizacion.Moneda).Format(monto, FormatoPeru.Cultura);

    private static void AgregarSiHay(Cell celda, string? texto)
    {
        if (!string.IsNullOrWhiteSpace(texto))
        {
            celda.AddParagraph(texto);
        }
    }

    private static string? Unir(string separador, params string?[] partes)
    {
        var conTexto = partes.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return conTexto.Count == 0 ? null : string.Join(separador, conTexto);
    }
}
