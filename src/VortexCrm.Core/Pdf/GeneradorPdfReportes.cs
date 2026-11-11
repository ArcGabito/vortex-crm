using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using VortexCrm.Core.Services;
using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Pdf;

/// <summary>El reporte comercial del periodo en PDF (A4): resumen, embudo y ranking por vendedor (fase 5d).</summary>
public static class GeneradorPdfReportes
{
    private static readonly Color Marca = new(0x51, 0x2B, 0xD4);
    private static readonly Color GrisClaro = new(0xF3, 0xF4, 0xF6);
    private static readonly Color GrisTexto = new(0x6B, 0x72, 0x80);

    public static byte[] Generar(ReporteVentas r, string empresa, CultureInfo cultura)
    {
        ResolvedorFuentes.Registrar();
        var documento = new Document();
        documento.Info.Title = T("reportes.titulo");
        documento.Info.Author = empresa;
        var normal = documento.Styles[StyleNames.Normal]!;
        normal.Font.Name = ResolvedorFuentes.Familia;
        normal.Font.Size = 9;

        var seccion = documento.AddSection();
        seccion.PageSetup = documento.DefaultPageSetup.Clone();
        seccion.PageSetup.PageFormat = PageFormat.A4;
        seccion.PageSetup.TopMargin = seccion.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        seccion.PageSetup.LeftMargin = seccion.PageSetup.RightMargin = Unit.FromCentimeter(1.8);

        var titulo = seccion.AddParagraph(empresa);
        titulo.Format.Font.Size = 16;
        titulo.Format.Font.Bold = true;
        titulo.Format.Font.Color = Marca;
        var sub = seccion.AddParagraph($"{T("reportes.titulo")} · {r.Desde.ToString("d", cultura)} – {r.Hasta.ToString("d", cultura)}");
        sub.Format.Font.Color = GrisTexto;
        sub.Format.SpaceAfter = Unit.FromPoint(12);

        string N(decimal? v, string formato = "#,0.##") => v is { } x ? x.ToString(formato, cultura) : "—";

        Titulo(seccion, T("reportes.resumen"));
        var resumen = Tabla(seccion, [9.4, 8]);
        foreach (var (k, v) in new (string, string)[]
        {
            (T("reportes.monto_ganado"), $"{r.Moneda} {N(r.MontoGanado, "#,0.00")}"), (T("reportes.ganadas"), N(r.Ganadas)),
            (T("reportes.perdidas"), N(r.Perdidas)), (T("reportes.tasa_cierre"), r.TasaCierre is { } t ? $"{N(t)} %" : "—"),
            (T("reportes.ticket_promedio"), r.TicketPromedio is { } tp ? $"{r.Moneda} {N(tp, "#,0.00")}" : "—"),
            (T("reportes.ciclo_dias"), N(r.CicloDias)), (T("reportes.leads_nuevos"), N(r.LeadsNuevos)),
            (T("reportes.conversion_leads"), r.ConversionLeads is { } cl ? $"{N(cl)} %" : "—"),
            (T("reportes.cotizaciones_enviadas"), N(r.CotizacionesEnviadas)),
            (T("reportes.aceptacion_cotizaciones"), r.AceptacionCotizaciones is { } ac ? $"{N(ac)} %" : "—"),
            (T("reportes.interacciones"), N(r.Interacciones)), (T("reportes.tareas"), N(r.TareasHechas)),
        })
        {
            var fila = resumen.AddRow();
            fila.Cells[0].AddParagraph(k);
            var valor = fila.Cells[1].AddParagraph(v);
            valor.Format.Alignment = ParagraphAlignment.Right;
            valor.Format.Font.Bold = true;
        }

        Titulo(seccion, T("reportes.embudo"));
        var embudo = Tabla(seccion, [9.4, 3, 5], encabezado: [T("reportes.etapa"), T("reportes.oportunidades"), T("reportes.monto")]);
        foreach (var e in r.Embudo)
        {
            var fila = embudo.AddRow();
            fila.Cells[0].AddParagraph(T("enum.etapa." + e.Etapa.ToString().ToLowerInvariant()));
            Derecha(fila.Cells[1], N(e.Oportunidades));
            Derecha(fila.Cells[2], N(e.Monto, "#,0.00"));
        }

        Titulo(seccion, T("reportes.ranking"));
        var ranking = Tabla(seccion, [4.6, 1.8, 3, 1.8, 2, 2, 2.2],
            encabezado: [T("reportes.vendedor"), T("reportes.ganadas"), T("reportes.monto_ganado"), T("reportes.perdidas"), T("reportes.tasa_cierre"),
                T("reportes.ciclo_dias"), T("reportes.interacciones")]);
        foreach (var f in r.Vendedores)
        {
            var fila = ranking.AddRow();
            fila.Cells[0].AddParagraph(f.Nombre);
            Derecha(fila.Cells[1], N(f.Ganadas));
            Derecha(fila.Cells[2], N(f.MontoGanado, "#,0.00"));
            Derecha(fila.Cells[3], N(f.Perdidas));
            Derecha(fila.Cells[4], f.TasaCierre is { } t ? $"{N(t)} %" : "—");
            Derecha(fila.Cells[5], N(f.CicloDias));
            Derecha(fila.Cells[6], N(f.Interacciones));
        }

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

    private static void Titulo(Section seccion, string texto)
    {
        var p = seccion.AddParagraph(texto);
        p.Format.Font.Size = 11;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = Unit.FromPoint(10);
        p.Format.SpaceAfter = Unit.FromPoint(4);
    }

    private static Table Tabla(Section seccion, double[] anchos, string[]? encabezado = null)
    {
        var tabla = seccion.AddTable();
        tabla.Borders.Bottom.Width = 0.5;
        tabla.Borders.Bottom.Color = GrisClaro;
        foreach (var a in anchos)
            tabla.AddColumn(Unit.FromCentimeter(a));
        if (encabezado is not null)
        {
            var fila = tabla.AddRow();
            fila.HeadingFormat = true;
            fila.Shading.Color = GrisClaro;
            fila.Format.Font.Bold = true;
            for (var i = 0; i < encabezado.Length; i++)
            {
                var p = fila.Cells[i].AddParagraph(encabezado[i]);
                if (i > 0)
                    p.Format.Alignment = ParagraphAlignment.Right;
            }
        }
        return tabla;
    }

    private static void Derecha(Cell celda, string texto) => celda.AddParagraph(texto).Format.Alignment = ParagraphAlignment.Right;
}
