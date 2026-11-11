using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Segmentos;
using VortexCrm.Core.Integration;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Segmentación de la cartera (fase 5c): etiquetas y segmentos guardados (CRM-05), campos personalizados en clientes,
/// oportunidades y cotizaciones (CRM-06) y jerarquía de empresas, matriz y sucursales (CRM-07). Todo son registros del CRM
/// (<c>crm.record.upserted</c>): el cliente sigue siendo el party del hub.
/// </summary>
public sealed class ServicioSegmentos(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    public const int NivelesMaximos = 5;

    // ---------- Etiquetas y matriz del cliente ----------

    public async Task<PerfilCliente> PerfilAsync(Guid clienteId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await PerfilAsync(c, null, clienteId);
    }

    private static async Task<PerfilCliente> PerfilAsync(SqliteConnection c, SqliteTransaction? tx, Guid clienteId) =>
        await c.FirstOrDefaultAsync("SELECT * FROM perfiles_cliente WHERE id = $id",
            r => new PerfilCliente(clienteId, JsonSerializer.Deserialize<List<string>>(r.Str("etiquetas")) ?? [], r.GuidOrNull("matriz_id")), tx,
            ("$id", clienteId))
        ?? new PerfilCliente(clienteId, [], null);

    public Task GuardarEtiquetasAsync(Guid clienteId, IEnumerable<string> etiquetas, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        var limpias = Etiquetas.Normalizar(etiquetas);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireClienteAsync(c, tx, clienteId);
            var perfil = await PerfilAsync(c, tx, clienteId);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.PerfilCliente, clienteId,
                new Registros.PerfilClienteDatos(clienteId, limpias, perfil.MatrizId));
            await AuditAsync(c, tx, now, "customer.tags", "customer", clienteId, null, string.Join(", ", limpias));
            return 0;
        }, ct);
    }

    /// <summary>
    /// Pone al cliente como sucursal de <paramref name="matrizId"/> (null: deja de ser sucursal). La matriz no puede ser el
    /// mismo cliente ni una de sus sucursales, y la cadena tiene como mucho <see cref="NivelesMaximos"/> niveles.
    /// </summary>
    public Task AsignarMatrizAsync(Guid clienteId, Guid? matrizId, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireClienteAsync(c, tx, clienteId);
            if (matrizId is { } matriz)
            {
                Require(matriz != clienteId, T("error.matriz_misma"));
                Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties WHERE id = $id AND is_deleted = 0", tx, ("$id", matriz)) > 0,
                    T("error.matriz_no_existe"));
                // Subiendo desde la matriz no se puede llegar al cliente (sería un ciclo) ni pasar del máximo de niveles.
                var actual = (Guid?)matriz;
                var niveles = 1;
                while (actual is { } a)
                {
                    Require(a != clienteId, T("error.matriz_ciclo"));
                    actual = (await PerfilAsync(c, tx, a)).MatrizId;
                    Require(++niveles <= NivelesMaximos, T("error.matriz_niveles", NivelesMaximos));
                }
            }
            var perfil = await PerfilAsync(c, tx, clienteId);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.PerfilCliente, clienteId,
                new Registros.PerfilClienteDatos(clienteId, perfil.Etiquetas, matrizId));
            await AuditAsync(c, tx, now, "customer.parent", "customer", clienteId, matrizId?.ToString(), null);
            return 0;
        }, ct);
    }

    /// <summary>La matriz del cliente (si es sucursal) y sus sucursales directas.</summary>
    public async Task<GrupoEmpresarial> GrupoAsync(Guid clienteId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var perfil = await PerfilAsync(c, null, clienteId);
        var matriz = perfil.MatrizId is { } m
            ? await c.FirstOrDefaultAsync("SELECT * FROM parties WHERE id = $id", MasterDataService.ReadParty, null, ("$id", m))
            : null;
        var sucursales = await c.QueryAsync("""
            SELECT p.* FROM parties p JOIN perfiles_cliente pc ON pc.id = p.id
            WHERE pc.matriz_id = $id AND p.is_deleted = 0 ORDER BY p.name COLLATE NOCASE
            """, MasterDataService.ReadParty, null, ("$id", clienteId));
        return new GrupoEmpresarial(matriz, sucursales);
    }

    /// <summary>Etiquetas en uso con cuántos clientes las tienen (para los filtros).</summary>
    public async Task<IReadOnlyList<UsoEtiqueta>> EtiquetasAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT j.value AS etiqueta, COUNT(*) AS clientes FROM perfiles_cliente pc, json_each(pc.etiquetas) j
            JOIN parties p ON p.id = pc.id WHERE p.is_deleted = 0
            GROUP BY j.value ORDER BY clientes DESC, j.value
            """, r => new UsoEtiqueta(r.Str("etiqueta"), r.Int("clientes")));
    }

    private async Task RequireClienteAsync(SqliteConnection c, SqliteTransaction tx, Guid clienteId)
    {
        var duenio = await c.FirstOrDefaultAsync("SELECT owner_id, owner_name FROM parties WHERE id = $id AND is_deleted = 0",
            r => new { Id = r.GuidOrNull("owner_id"), Nombre = r.StrOrNull("owner_name") }, tx, ("$id", clienteId));
        Require(duenio is not null, T("error.cliente_no_existe"));
        RequireOwner(duenio!.Id, duenio.Nombre);
    }

    // ---------- Segmentos ----------

    /// <summary>Clientes que cumplen el filtro, con sus etiquetas y su matriz.</summary>
    public async Task<PagedResult<ClienteSegmento>> BuscarAsync(FiltroClientes filtro, int pagina = 1, int porPagina = 15, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (where, args) = Condiciones(filtro);
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM parties p LEFT JOIN perfiles_cliente pc ON pc.id = p.id {where}", null, args);
        var filas = await c.QueryAsync($"""
            SELECT p.*, pc.etiquetas AS perfil_etiquetas, pc.matriz_id AS perfil_matriz, m.name AS matriz_nombre
            FROM parties p LEFT JOIN perfiles_cliente pc ON pc.id = p.id LEFT JOIN parties m ON m.id = pc.matriz_id
            {where} ORDER BY p.name COLLATE NOCASE LIMIT $limite OFFSET $desde
            """, LeerSegmento, null, [.. args, ("$limite", porPagina), ("$desde", (pagina - 1) * porPagina)]);
        return new PagedResult<ClienteSegmento>(filas, total, pagina, porPagina);
    }

    /// <summary>El segmento en CSV (separado por comas, UTF-8) para una campaña o para Excel.</summary>
    public async Task<string> ExportarCsvAsync(FiltroClientes filtro, CancellationToken ct = default)
    {
        var todos = await BuscarAsync(filtro, 1, 100_000, ct);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', T("csv.cliente"), T("csv.documento"), T("csv.correo"), T("csv.telefono"), T("csv.ciudad"), T("csv.responsable"),
            T("csv.etiquetas"), T("csv.matriz")));
        foreach (var s in todos.Items)
            sb.AppendLine(string.Join(',', new[] { s.Cliente.Name, s.Cliente.DocumentNumber, s.Cliente.Email, s.Cliente.Phone, s.Cliente.City,
                s.Cliente.OwnerName, string.Join("; ", s.Etiquetas), s.MatrizNombre }.Select(Csv)));
        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        var v = value ?? "";
        // Una celda que empieza con =, +, - o @ se toma como fórmula en Excel: se antepone un apóstrofo.
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
            v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    private (string Where, (string, object?)[] Args) Condiciones(FiltroClientes f)
    {
        var etiquetas = Etiquetas.Normalizar(f.Etiquetas);
        var desde = f.SinActividadDias is > 0 and var dias ? SqliteExtensions.Time(Clock.GetUtcNow().AddDays(-dias)) : null;
        const string where = """
            WHERE p.is_deleted = 0 AND p.is_customer = 1
              AND ($busqueda IS NULL OR p.name LIKE $like OR p.trade_name LIKE $like OR p.document_number LIKE $like OR p.email LIKE $like)
              AND ($ciudad IS NULL OR p.city LIKE $ciudad OR p.region LIKE $ciudad OR p.district LIKE $ciudad)
              AND ($responsable IS NULL OR p.owner_id = $responsable)
              AND ($sinResponsable = 0 OR p.owner_id IS NULL)
              AND ($grupo IS NULL OR p.id = $grupo OR pc.matriz_id = $grupo)
              AND ($nEtiquetas = 0 OR (SELECT COUNT(DISTINCT j.value) FROM json_each(COALESCE(pc.etiquetas, '[]')) j
                                       WHERE j.value IN (SELECT value FROM json_each($etiquetas))) >= CASE WHEN $todas = 1 THEN $nEtiquetas ELSE 1 END)
              AND ($campo IS NULL OR EXISTS (SELECT 1 FROM valores_campos v, json_each(v.datos, '$.valores') j
                                             WHERE v.id = p.id AND j.key = $campo AND lower(j.value) = lower($valor)))
              AND ($desdeActividad IS NULL OR (
                    NOT EXISTS (SELECT 1 FROM actividad a WHERE a.cliente_id = p.id AND a.ocurrido_en >= $desdeActividad)
                AND NOT EXISTS (SELECT 1 FROM interacciones i WHERE i.cliente_id = p.id AND i.fecha >= $desdeActividad)))
            """;
        return (where,
        [
            ("$busqueda", Clean(f.Busqueda)), ("$like", Like(f.Busqueda)), ("$ciudad", Clean(f.Ciudad) is { } ciudad ? $"%{ciudad}%" : null),
            ("$responsable", f.ResponsableId), ("$sinResponsable", f.SinResponsable), ("$grupo", f.GrupoDe),
            ("$etiquetas", JsonSerializer.Serialize(etiquetas)), ("$nEtiquetas", etiquetas.Count), ("$todas", f.TodasLasEtiquetas),
            ("$campo", f.CampoId?.ToString()), ("$valor", Clean(f.CampoValor) ?? ""), ("$desdeActividad", desde),
        ]);
    }

    private static ClienteSegmento LeerSegmento(SqliteDataReader r) => new(
        MasterDataService.ReadParty(r),
        r.StrOrNull("perfil_etiquetas") is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [],
        r.GuidOrNull("perfil_matriz"), r.StrOrNull("matriz_nombre"));

    // ---------- Segmentos guardados ----------

    /// <summary>Las listas propias y las compartidas.</summary>
    public async Task<IReadOnlyList<ListaGuardada>> ListasAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var todas = await c.QueryAsync("SELECT datos FROM listas_guardadas", r => JsonSerializer.Deserialize<ListaGuardada>(r.Str("datos"), VortexJson.Options)!);
        return todas.Where(l => l.Compartida || l.CreadaPorId == Context.UserId).OrderBy(l => l.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<Guid> GuardarListaAsync(ListaGuardada lista, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        Require(!string.IsNullOrWhiteSpace(lista.Nombre), T("error.lista_sin_nombre"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var existente = await ListaAsync(c, tx, lista.Id);
            if (existente is not null)
                RequireOwner(existente.CreadaPorId, existente.CreadaPor);
            var limpia = lista with
            {
                Id = lista.Id == Guid.Empty ? Guid.NewGuid() : lista.Id,
                Nombre = lista.Nombre.Trim(),
                Filtro = lista.Filtro with { Etiquetas = Etiquetas.Normalizar(lista.Filtro.Etiquetas) },
                CreadaPorId = existente?.CreadaPorId ?? Context.UserId,
                CreadaPor = existente?.CreadaPor ?? Context.UserName,
            };
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.ListaGuardada, limpia.Id, limpia);
            await AuditAsync(c, tx, now, "segment.saved", "segment", limpia.Id, limpia.Nombre, limpia.Compartida ? "compartida" : "privada");
            return limpia.Id;
        }, ct);
    }

    public Task EliminarListaAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var lista = await ListaAsync(c, tx, id);
            Require(lista is not null, T("error.lista_no_existe"));
            RequireOwner(lista!.CreadaPorId, lista.CreadaPor);
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.ListaGuardada, id);
            await AuditAsync(c, tx, now, "segment.deleted", "segment", id, lista.Nombre, null);
            return 0;
        }, ct);
    }

    private static async Task<ListaGuardada?> ListaAsync(SqliteConnection c, SqliteTransaction tx, Guid id) =>
        await c.ScalarAsync<string?>("SELECT datos FROM listas_guardadas WHERE id = $id", tx, ("$id", id)) is { } json
            ? JsonSerializer.Deserialize<ListaGuardada>(json, VortexJson.Options)
            : null;

    // ---------- Campos personalizados ----------

    /// <param name="incluirInactivos">También los desactivados (para administrarlos).</param>
    public async Task<IReadOnlyList<CampoPersonalizado>> CamposAsync(string? entidad = null, bool incluirInactivos = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (await CamposAsync(c, null))
            .Where(x => (entidad is null || x.Entidad == entidad) && (incluirInactivos || x.Activo))
            .OrderBy(x => x.Entidad).ThenBy(x => x.Orden).ThenBy(x => x.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static Task<List<CampoPersonalizado>> CamposAsync(SqliteConnection c, SqliteTransaction? tx) =>
        c.QueryAsync("SELECT datos FROM campos_personalizados", r => JsonSerializer.Deserialize<CampoPersonalizado>(r.Str("datos"), VortexJson.Options)!, tx);

    public Task<Guid> GuardarCampoAsync(CampoPersonalizado campo, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        Require(EntidadesCampo.Todas.Contains(campo.Entidad), T("error.campo_entidad"));
        Require(TiposCampo.Todos.Contains(campo.Tipo), T("error.campo_tipo"));
        Require(!string.IsNullOrWhiteSpace(campo.Nombre) && campo.Nombre.Trim().Length <= 60, T("error.campo_nombre"));
        var opciones = campo.Opciones?.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Require(campo.Tipo != TiposCampo.Lista || opciones is { Count: > 0 }, T("error.campo_opciones"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var limpio = campo with
            {
                Id = campo.Id == Guid.Empty ? Guid.NewGuid() : campo.Id,
                Nombre = campo.Nombre.Trim(),
                Opciones = campo.Tipo == TiposCampo.Lista ? opciones : null,
            };
            var otros = await CamposAsync(c, tx);
            Require(!otros.Any(o => o.Id != limpio.Id && o.Entidad == limpio.Entidad && string.Equals(o.Nombre, limpio.Nombre, StringComparison.OrdinalIgnoreCase)),
                T("error.campo_repetido", limpio.Nombre));
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.CampoPersonalizado, limpio.Id, limpio);
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.SettingsChanged, "custom_field", limpio.Id, limpio.Nombre,
                $"{limpio.Entidad}, {limpio.Tipo}{(limpio.Activo ? "" : ", inactivo")}");
            return limpio.Id;
        }, ct);
    }

    /// <summary>Valores de los campos de un cliente, oportunidad o cotización (por Id de campo).</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ValoresAsync(Guid registroId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ValoresAsync(c, null, registroId);
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> ValoresAsync(SqliteConnection c, SqliteTransaction? tx, Guid registroId) =>
        await c.ScalarAsync<string?>("SELECT datos FROM valores_campos WHERE id = $id", tx, ("$id", registroId)) is { } json
            ? JsonSerializer.Deserialize<Registros.ValoresCamposDatos>(json, VortexJson.Options)!.Valores
            : new Dictionary<Guid, string>();

    /// <summary>
    /// Guarda los valores del registro: cada uno se valida con su campo (tipo, opciones, obligatorio). Los campos que no vienen
    /// conservan su valor; un valor vacío lo borra.
    /// </summary>
    public Task GuardarValoresAsync(string entidad, Guid registroId, IReadOnlyDictionary<Guid, string?> valores, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        Require(EntidadesCampo.Todas.Contains(entidad), T("error.campo_entidad"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var duenio = entidad switch
            {
                EntidadesCampo.Cliente => await c.FirstOrDefaultAsync("SELECT owner_id AS id, owner_name AS nombre FROM parties WHERE id = $id AND is_deleted = 0",
                    LeerDuenio, tx, ("$id", registroId)),
                EntidadesCampo.Oportunidad => await c.FirstOrDefaultAsync("SELECT responsable_id AS id, responsable_nombre AS nombre FROM oportunidades WHERE id = $id",
                    LeerDuenio, tx, ("$id", registroId)),
                _ => await c.FirstOrDefaultAsync("SELECT responsable_id AS id, responsable_nombre AS nombre FROM cotizaciones WHERE id = $id",
                    LeerDuenio, tx, ("$id", registroId)),
            };
            Require(duenio is not null, T("error.registro_no_existe"));
            RequireOwner(duenio!.Id, duenio.Nombre);

            var campos = (await CamposAsync(c, tx)).Where(x => x.Entidad == entidad && x.Activo).ToDictionary(x => x.Id);
            var resultado = (await ValoresAsync(c, tx, registroId)).ToDictionary();
            foreach (var (campoId, valor) in valores)
            {
                Require(campos.TryGetValue(campoId, out var campo), T("error.campo_no_existe"));
                var (normalizado, error) = campo!.Normalizar(valor);
                if (error is not null)
                    throw new CrmValidationException(T("error.campo_valor_" + error, campo.Nombre));
                if (normalizado is null)
                    resultado.Remove(campoId);
                else
                    resultado[campoId] = normalizado;
            }
            var faltan = campos.Values.Where(x => x.Obligatorio && !resultado.ContainsKey(x.Id)).Select(x => x.Nombre).ToList();
            Require(faltan.Count == 0, T("error.campo_valor_obligatorio", string.Join(", ", faltan)));
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.ValoresCampos, registroId,
                new Registros.ValoresCamposDatos(registroId, entidad, resultado));
            return 0;
        }, ct);
    }

    private sealed record Duenio(Guid? Id, string? Nombre);

    private static Duenio LeerDuenio(SqliteDataReader r) => new(r.GuidOrNull("id"), r.StrOrNull("nombre"));

    /// <summary>Un valor tal como se muestra: fechas dd/MM/yyyy, sí/no y números con la cultura dada.</summary>
    public static string Mostrar(CampoPersonalizado campo, string valor, CultureInfo cultura, Func<string, string> texto) => campo.Tipo switch
    {
        TiposCampo.Fecha when DateOnly.TryParse(valor, CultureInfo.InvariantCulture, out var d) => d.ToString("d", cultura),
        TiposCampo.Numero when decimal.TryParse(valor, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => n.ToString("#,0.##", cultura),
        TiposCampo.SiNo => texto(valor == "true" ? "segmentos.si" : "segmentos.no"),
        _ => valor,
    };
}
