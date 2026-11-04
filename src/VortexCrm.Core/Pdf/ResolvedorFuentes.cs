using PdfSharp.Fonts;

namespace VortexCrm.Core.Pdf;

/// <summary>
/// Entrega a PDFsharp las fuentes Open Sans incrustadas en el ensamblado, para que el PDF
/// salga igual en Android, Windows y el servidor (que no tienen las mismas fuentes instaladas).
/// </summary>
internal sealed class ResolvedorFuentes : IFontResolver
{
    public const string Familia = "Open Sans";

    private const string Regular = "OpenSans-Regular";
    private const string Negrita = "OpenSans-Bold";

    private static readonly Lock Candado = new();

    /// <summary>Registra el resolvedor una sola vez; PDFsharp no permite cambiarlo después de usarlo.</summary>
    public static void Registrar()
    {
        lock (Candado)
        {
            GlobalFontSettings.FontResolver ??= new ResolvedorFuentes();
        }
    }

    // Cualquier familia que se pida se resuelve con Open Sans
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? Negrita : Regular, mustSimulateBold: false, mustSimulateItalic: isItalic);

    public byte[]? GetFont(string faceName)
    {
        using var recurso = typeof(ResolvedorFuentes).Assembly.GetManifestResourceStream($"VortexCrm.Fuentes.{faceName}.ttf")
            ?? throw new InvalidOperationException($"No se encontró la fuente incrustada {faceName}.");
        using var memoria = new MemoryStream();
        recurso.CopyTo(memoria);
        return memoria.ToArray();
    }
}
