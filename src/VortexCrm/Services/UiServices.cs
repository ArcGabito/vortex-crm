using System.Globalization;
using System.Text;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Domain.Ventas;

using static VortexCrm.Services.Txt;

namespace VortexCrm.Services;

public enum ToastKind
{
    Success,
    Error,
    Info,
}

public sealed record Toast(Guid Id, ToastKind Kind, string Message);

/// <summary>Avisos breves en la esquina inferior derecha.</summary>
public sealed class Toasts
{
    private readonly List<Toast> _items = [];

    public event Action? Changed;
    public IReadOnlyList<Toast> Items => _items;

    public void Success(string message) => Show(ToastKind.Success, message);
    public void Error(string message) => Show(ToastKind.Error, message);
    public void Info(string message) => Show(ToastKind.Info, message);

    public void Dismiss(Guid id)
    {
        _items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    private void Show(ToastKind kind, string message)
    {
        var toast = new Toast(Guid.NewGuid(), kind, message);
        _items.Add(toast);
        Changed?.Invoke();
        _ = Task.Delay(kind == ToastKind.Error ? 7000 : 4000).ContinueWith(_ => Dismiss(toast.Id), TaskScheduler.Default);
    }
}

/// <summary>Exporta archivos: en escritorio los guarda en Documentos/Vortex CRM y los abre; en móvil, los comparte.</summary>
public sealed class FileExporter
{
    public Task<string> ExportAsync(string fileName, string content, string contentType = "text/csv") =>
        ExportBytesAsync(fileName, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray(),
            contentType);

    public async Task<string> ExportBytesAsync(string fileName, byte[] content, string contentType)
    {
        var cache = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(cache, content);
#if WINDOWS || MACCATALYST
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vortex CRM");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, fileName);
        File.Copy(cache, target, overwrite: true);
        await Launcher.Default.OpenAsync(new OpenFileRequest(fileName, new ReadOnlyFile(target)));
        return target;
#else
        await Share.Default.RequestAsync(new ShareFileRequest { Title = fileName, File = new ShareFile(cache, contentType) });
        return cache;
#endif
    }
}

/// <summary>Formatos y textos de la interfaz (es-PE).</summary>
public static class Fmt
{
    /// <summary>Cultura de la interfaz: idioma del usuario y país de la empresa (<see cref="SetRegion"/>).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("es-PE");

    public static string Language { get; private set; } = VortexApi.Suite.Regional.Languages.Spanish;

    /// <summary>Zona horaria de la empresa (IANA): las fechas se muestran en su hora, no en la del equipo.</summary>
    public static string? TimeZone { get; private set; }

    /// <summary>Moneda de la empresa: la de los montos del pipeline y los reportes.</summary>
    public static string Currency { get; private set; } = "PEN";

    public static void SetRegion(string language, string? countryCode, string? timeZone, string? currency)
    {
        Language = VortexApi.Suite.Regional.Languages.Normalize(language);
        VortexCrm.Core.Texts.CoreText.Language = Language;
        Culture = VortexApi.Suite.Regional.RegionalSettings.CultureFor(Language, countryCode);
        TimeZone = timeZone;
        Currency = currency ?? Currency;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public static string Qty(decimal value) => value.ToString(value % 1 == 0 ? "#,0" : "#,0.##", Culture);

    /// <summary>Cifras de las tarjetas: 1,284 · 12.9 mil · 4.2 M.</summary>
    public static string Compact(decimal value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", Culture) + " M",
        >= 10_000 => (value / 1_000).ToString("0.#", Culture) + T("fmt.mil"),
        _ => Qty(Math.Round(value, 2)),
    };

    /// <summary>"1 categoría", "3 categorías".</summary>
    public static string Count(int value, string singular, string plural) => $"{Qty(value)} {(value == 1 ? singular : plural)}";

    public static string Money(decimal value, string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Format(value, Culture);

    public static string MoneyCompact(decimal value, string currency) => $"{Symbol(currency)} {Compact(value)}";

    public static string Symbol(string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Symbol;

    public static DateTimeOffset Local(DateTimeOffset value) =>
        TimeZone is null ? value.ToLocalTime() : VortexApi.Suite.Regional.RegionalSettings.ToCompanyTime(value, TimeZone);

    public static string Date(DateTimeOffset value) => Local(value).ToString("d MMM yyyy", Culture);
    public static string DateTime(DateTimeOffset value) => Local(value).ToString("d MMM yyyy, HH:mm", Culture);

    public static string Ago(DateTimeOffset value)
    {
        var span = DateTimeOffset.Now - value;
        return span.TotalSeconds < 60 ? T("fmt.hace_un_momento")
            : span.TotalMinutes < 60 ? T("fmt.hace_min", (int)span.TotalMinutes)
            : span.TotalHours < 24 ? T("fmt.hace", (int)span.TotalHours)
            : Date(value);
    }

    public static string Initials(string? text)
    {
        var words = (text ?? "?").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}",
        };
    }

    /// <summary>Monto en la moneda de la empresa.</summary>
    public static string Amount(decimal value) => Money(value, Currency);

    /// <summary>Mes con su año: «noviembre 2026».</summary>
    public static string Month(DateOnly month) => month.ToString("MMMM yyyy", Culture);

    public static string Date(DateOnly value) => value.ToString("dd MMM yyyy", Culture);

    /// <summary>"Hoy", "Mañana", "lun. 5 oct."…</summary>
    public static string Relative(DateOnly value) => (value.DayNumber - Today.DayNumber) switch
    {
        0 => T("fmt.hoy"),
        1 => T("fmt.manana"),
        -1 => T("fmt.ayer"),
        _ when value.Year == Today.Year => value.ToString("ddd d MMM", Culture),
        _ => value.ToString("d MMM yyyy", Culture),
    };

    /// <summary>Nombre de la etapa en el idioma de la interfaz (el de Core, en español, queda para la auditoría).</summary>
    public static string Stage(EtapaOportunidad etapa) => T("enum.etapa." + etapa.ToString().ToLowerInvariant());

    public static string TaskType(TipoTarea tipo) => T("enum.tarea." + tipo.ToString().ToLowerInvariant());

    public static string QuoteStatus(EstadoCotizacion estado) => T("enum.cotizacion." + estado.ToString().ToLowerInvariant());

    /// <summary>"Precios con IGV incluido", "Prices exclude VAT (16 % added)"…</summary>
    public static string TaxMode(ModoIgv modo, string impuesto = "IGV", decimal tasa = Impuestos.TasaIgv) => modo switch
    {
        ModoIgv.Incluido => T("enum.modo.incluido", impuesto),
        ModoIgv.Adicional => T("enum.modo.adicional", impuesto, (tasa * 100).ToString("0.##", Culture)),
        _ => T("enum.modo.no_aplica", impuesto),
    };

    /// <summary>Hoy en Perú.</summary>
    public static DateOnly Today => VortexApi.Suite.Regional.RegionalSettings.Today(TimeZone ?? "America/Lima");

    public static string TaskIcon(TipoTarea tipo) => tipo switch
    {
        TipoTarea.Llamada => "phone",
        TipoTarea.WhatsApp => "message",
        TipoTarea.Reunion => "users",
        TipoTarea.Visita => "pin",
        TipoTarea.Correo => "mail",
        _ => "check",
    };

    public static string Activity(string tipo) => tipo switch
    {
        TiposActividad.Venta => "cart",
        TiposActividad.Comprobante => "receipt",
        TiposActividad.Cobro => "wallet",
        TiposActividad.Despacho => "truck",
        _ => "bell",
    };

    public static string TaxCategory(string category) => category switch
    {
        VortexApi.Suite.Taxes.TaxCategories.Standard => T("fmt.tasa_general"),
        VortexApi.Suite.Taxes.TaxCategories.Reduced => T("fmt.tasa_reducida"),
        VortexApi.Suite.Taxes.TaxCategories.SuperReduced => T("fmt.tasa_superreducida"),
        VortexApi.Suite.Taxes.TaxCategories.Increased => T("fmt.tasa_incrementada"),
        VortexApi.Suite.Taxes.TaxCategories.Zero => T("fmt.tasa_cero_exportacion"),
        VortexApi.Suite.Taxes.TaxCategories.Exempt => T("fmt.exonerado_exento"),
        _ => T("fmt.inafecto_no_sujeto"),
    };

    public static string AuditAction(string action) => action switch
    {
        "stage.changed" => T("fmt.cambio_de_etapa"),
        "quote.enviada" => T("fmt.cotizacion_enviada"),
        "quote.aceptada" => T("fmt.cotizacion_aceptada"),
        "quote.rechazada" => T("fmt.cotizacion_rechazada"),
        "quote.order_created" => T("fmt.pedido_generado"),
        "settings.changed" => T("fmt.ajustes_de_ventas"),
        "record.deleted" => T("fmt.eliminacion"),
        _ => action,
    };

    public static string SiteKind(string kind) => kind switch
    {
        "Warehouse" => T("fmt.almacen"),
        "Store" => T("fmt.tienda"),
        "Office" => T("fmt.oficina"),
        _ => T("fmt.otro"),
    };
}
