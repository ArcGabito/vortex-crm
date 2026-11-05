using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using VortexApi.Suite.Regional;
using VortexApi.Suite.Text;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Texts;

namespace VortexCrm.Core.Tests;

/// <summary>Los textos de la interfaz están completos en español, inglés y portugués.</summary>
public sealed partial class TextsTests
{
    private static readonly Assembly Core = typeof(CrmWorkspace).Assembly;

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = Core.GetManifestResourceStream($"VortexCrm.Core.Texts.{language}.json")
            ?? throw new InvalidOperationException($"Falta Texts/{language}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    [Theory]
    [InlineData(Languages.English)]
    [InlineData(Languages.Portuguese)]
    public void Cada_texto_existe_en_cada_idioma_con_los_mismos_marcadores(string language)
    {
        var spanish = Load(Languages.Spanish);
        var other = Load(language);

        Assert.Empty(spanish.Keys.Except(other.Keys));
        Assert.Empty(other.Keys.Except(spanish.Keys));
        var mismatched = spanish.Where(e => Markers(e.Value) != Markers(other[e.Key])).Select(e => e.Key);
        Assert.Empty(mismatched);
    }

    [Fact]
    public void Cada_clave_usada_en_la_interfaz_existe()
    {
        var catalog = new TextCatalog().AddResources(Core, "VortexCrm.Core.Texts");
        var used = new[] { "VortexCrm", "VortexCrm.Core" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => KeyUse().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.'))
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        var missing = used.Where(k => catalog.Get(Languages.Spanish, k) == k).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void El_idioma_se_elige_y_si_falta_una_clave_se_usa_el_espanol()
    {
        var catalog = new TextCatalog().AddResources(Core, "VortexCrm.Core.Texts");
        var key = Load(Languages.Spanish).Keys.First(k => k.StartsWith("settings."));
        Assert.NotEqual(catalog.Get(Languages.Spanish, key), catalog.Get(Languages.English, key));
        Assert.Equal(catalog.Get(Languages.Spanish, key), catalog.Get("fr", key));
        Assert.Equal(Languages.Portuguese, Languages.DefaultFor("BR"));
        Assert.Equal(Languages.English, Languages.DefaultFor("US"));
        Assert.Equal(Languages.Spanish, Languages.DefaultFor("PE"));
    }

    [Fact]
    public void Los_nombres_y_mensajes_de_la_logica_salen_en_el_idioma_elegido()
    {
        using (CoreText.Use(Languages.English))
        {
            Assert.Equal("Negotiation", VortexCrm.Core.Domain.Ventas.EtapaOportunidad.Negociacion.Nombre());
            Assert.Equal("Meeting", VortexCrm.Core.Domain.Tareas.TipoTarea.Reunion.Nombre());
            Assert.Equal("Prices include VAT", VortexCrm.Core.Domain.Ventas.ModoIgv.Incluido.Nombre("VAT"));
        }
        Assert.Equal("Negociación", VortexCrm.Core.Domain.Ventas.EtapaOportunidad.Negociacion.Nombre());
    }

    private static string Markers(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Order());

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VortexCrm.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    [GeneratedRegex("""\bT\("([a-z0-9_.]+)"[,)]""")]
    private static partial Regex KeyUse();
}
