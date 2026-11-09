using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Sync;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Cartera de clientes (fase 2c): quién atiende a cada cliente, reasignación de carteras y clientes duplicados. El
/// responsable vive en el <c>party</c> del hub (bloque <c>account</c>), así lo ven todos los equipos y sistemas. Al
/// cambiarlo se devuelve el cliente completo tal como llegó del hub (para no borrar lo que el CRM no maneja) y sin el
/// bloque <c>credit</c> (las condiciones del ERP quedan como estén).
/// </summary>
public sealed class ServicioCartera(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    /// <summary>Quienes ya aparecen como responsables (de clientes, oportunidades, leads o metas) y el usuario actual.</summary>
    public async Task<IReadOnlyList<Vendedor>> VendedoresAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var vistos = await c.QueryAsync("""
            SELECT owner_id AS id, owner_name AS nombre FROM parties WHERE owner_id IS NOT NULL
            UNION SELECT responsable_id, responsable_nombre FROM oportunidades WHERE responsable_id IS NOT NULL
            UNION SELECT responsable_id, responsable_nombre FROM leads WHERE responsable_id IS NOT NULL
            UNION SELECT responsable_id, responsable_nombre FROM metas WHERE responsable_id IS NOT NULL
            """, r => new Vendedor(r.Guid("id"), r.StrOrNull("nombre") ?? "?"));
        if (Context.UserId is { } yo)
            vistos.Add(new Vendedor(yo, Context.UserName));
        return vistos.GroupBy(v => v.Id).Select(g => g.OrderByDescending(v => v.Id == Context.UserId).First())
            .OrderBy(v => v.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Fija (o quita, con null) el responsable del cliente. Con <see cref="CrmPermissions.AssignCustomers"/> se asigna
    /// a cualquiera; un vendedor solo puede tomar para sí un cliente que no tiene responsable, o soltar uno suyo.
    /// </summary>
    public Task AsignarAsync(Guid clienteId, Guid? responsableId, string? responsableNombre, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var actual = await ClienteAsync(c, tx, clienteId);
            if (!Context.Can(CrmPermissions.AssignCustomers))
            {
                var tomaParaSi = responsableId == Context.UserId && actual.OwnerId is null;
                var sueltaLoSuyo = responsableId is null && actual.OwnerId == Context.UserId;
                Require(tomaParaSi || sueltaLoSuyo, T("error.solo_el_gerente_asigna_clientes"));
            }
            await CambiarResponsableAsync(c, tx, now, clienteId, responsableId, responsableNombre);
            return 0;
        }, ct);
    }

    /// <summary>
    /// Reparte los clientes sin responsable por territorio (CRM-53): el cliente cuya ciudad, región o distrito está en las zonas
    /// de un equipo va al vendedor de ese equipo con menos clientes (o al líder si no tiene vendedores). Devuelve cuántos asignó.
    /// </summary>
    public Task<int> AsignarPorTerritorioAsync(CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.AssignCustomers);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var equipos = (await ServicioEquipos.EquiposAsync(c, tx)).Where(e => e.Zonas.Count > 0).ToList();
            if (equipos.Count == 0)
                return 0;
            var carga = (await c.QueryAsync("SELECT owner_id, COUNT(*) AS n FROM parties WHERE owner_id IS NOT NULL AND is_deleted = 0 GROUP BY owner_id",
                r => (Id: r.Guid("owner_id"), N: r.Int("n")), tx)).ToDictionary(x => x.Id, x => x.N);
            var libres = await c.QueryAsync("""
                SELECT * FROM parties WHERE is_customer = 1 AND is_active = 1 AND is_deleted = 0 AND owner_id IS NULL ORDER BY name
                """, MasterDataService.ReadParty, tx);
            var asignados = 0;
            foreach (var cliente in libres)
            {
                if (equipos.FirstOrDefault(e => e.Cubre(cliente)) is not { } equipo)
                    continue;
                var candidatos = equipo.Miembros.Count > 0 ? equipo.Miembros
                    : [new Domain.Vendedor(equipo.LiderId!.Value, equipo.LiderNombre ?? "?")];
                var elegido = candidatos.OrderBy(v => carga.GetValueOrDefault(v.Id)).ThenBy(v => v.Nombre).First();
                await CambiarResponsableAsync(c, tx, now, cliente.Id, elegido.Id, elegido.Nombre, auditar: false);
                carga[elegido.Id] = carga.GetValueOrDefault(elegido.Id) + 1;
                asignados++;
            }
            await AuditAsync(c, tx, now, "customer.assigned_by_territory", "party", null, null, $"{asignados} clientes");
            return asignados;
        }, ct);
    }

    /// <summary>
    /// Pasa todos los clientes de un vendedor a otro (o los deja sin responsable) y, con ellos, sus oportunidades
    /// abiertas. Devuelve cuántos clientes se movieron.
    /// </summary>
    public Task<int> ReasignarCarteraAsync(Guid desdeId, Guid? hastaId, string? hastaNombre, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.AssignCustomers);
        Require(desdeId != hastaId, T("error.elige_otro_vendedor"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var clientes = await c.QueryAsync("SELECT id FROM parties WHERE owner_id = $id AND is_deleted = 0", r => r.Guid("id"), tx,
                ("$id", desdeId));
            foreach (var cliente in clientes)
            {
                await CambiarResponsableAsync(c, tx, now, cliente, hastaId, hastaNombre, auditar: false);
                var abiertas = await c.QueryAsync("""
                    SELECT * FROM oportunidades WHERE cliente_id = $cliente AND responsable_id = $desde AND cerrado_en IS NULL
                    """, Registros.LeerOportunidad, tx, ("$cliente", cliente), ("$desde", desdeId));
                foreach (var o in abiertas)
                {
                    (o.ResponsableId, o.ResponsableNombre) = (hastaId, hastaNombre);
                    await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Oportunidad, o.Id, Registros.Datos(o));
                }
            }
            if (clientes.Count > 0)
                await AuditAsync(c, tx, now, "customer.reassigned", "party", null, null,
                    $"{clientes.Count} clientes → {hastaNombre ?? T("cartera.sin_responsable")}");
            return clientes.Count;
        }, ct);
    }

    // ---------- Duplicados ----------

    /// <summary>
    /// Clientes activos que parecen el mismo: mismo documento, mismo nombre (sin tildes, mayúsculas, puntuación ni
    /// S.A.C./E.I.R.L.…), mismo correo o mismo teléfono (últimos 9 dígitos). Un cliente aparece en un solo grupo, y en un
    /// grupo no hay dos documentos distintos.
    /// </summary>
    public async Task<IReadOnlyList<GrupoDuplicados>> DuplicadosAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var clientes = await c.QueryAsync("SELECT * FROM parties WHERE is_deleted = 0 AND is_active = 1 AND is_customer = 1",
            MasterDataService.ReadParty);
        var grupos = new List<GrupoDuplicados>();
        var usados = new HashSet<Guid>();
        var criterios = new (string Motivo, Func<PartyRow, string?> Clave)[]
        {
            (MotivosDuplicado.Documento, p => p.DocumentNumber is { Length: > 0 } d ? $"{p.DocumentType}:{d}" : null),
            (MotivosDuplicado.Nombre, p => NombreNormalizado(p.Name) is { Length: >= 4 } n ? n : null),
            (MotivosDuplicado.Correo, p => p.Email?.Trim().ToLowerInvariant() is { Length: > 3 } e ? e : null),
            (MotivosDuplicado.Telefono, p => new string((p.Phone ?? "").Where(char.IsAsciiDigit).ToArray()) is { Length: >= 7 } t ? t[^Math.Min(9, t.Length)..] : null),
        };
        foreach (var (motivo, clave) in criterios)
        {
            foreach (var g in clientes.Where(p => !usados.Contains(p.Id)).Select(p => (p, k: clave(p))).Where(x => x.k is not null)
                         .GroupBy(x => x.k!).Where(g => g.Count() > 1))
            {
                // Dos documentos distintos son dos clientes distintos: entran los que no tienen documento y un solo
                // documento (el del primero que lo tiene).
                var ordenados = g.Select(x => x.p).OrderBy(p => p.DocumentNumber is null).ThenBy(p => p.Name).ToList();
                var primero = ordenados[0];
                var miembros = ordenados.Where(p => p.DocumentNumber is null ||
                    (p.DocumentType == primero.DocumentType && p.DocumentNumber == primero.DocumentNumber)).ToList();
                if (miembros.Count < 2)
                    continue;
                grupos.Add(new GrupoDuplicados(motivo, g.Key, miembros));
                usados.UnionWith(miembros.Select(p => p.Id));
            }
        }
        return grupos;
    }

    /// <summary>
    /// Fusiona <paramref name="duplicadoId"/> en <paramref name="principalId"/>: sus contactos, oportunidades,
    /// cotizaciones, tareas y leads pasan al principal (y se replican a los demás equipos), y el duplicado queda inactivo
    /// en el hub con una nota. Lo que otros sistemas registraron con el duplicado (ventas, comprobantes) sigue a su nombre.
    /// </summary>
    public Task<ResultadoFusion> FusionarAsync(Guid principalId, Guid duplicadoId, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.MergeCustomers);
        Require(principalId != duplicadoId, T("error.elige_dos_clientes_distintos"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var principal = await ClienteAsync(c, tx, principalId);
            var duplicado = await ClienteAsync(c, tx, duplicadoId);
            Require(principal.DocumentNumber is null || duplicado.DocumentNumber is null ||
                    (principal.DocumentType == duplicado.DocumentType && principal.DocumentNumber == duplicado.DocumentNumber),
                T("error.documentos_distintos_no_se_fusionan", principal.DocumentNumber, duplicado.DocumentNumber));

            var contactos = await c.QueryAsync("SELECT * FROM contactos WHERE cliente_id = $id", Registros.LeerContacto, tx, ("$id", duplicadoId));
            foreach (var x in contactos)
            {
                x.ClienteId = principalId;
                await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Contacto, x.Id, Registros.Datos(x));
            }
            var oportunidades = await c.QueryAsync("SELECT * FROM oportunidades WHERE cliente_id = $id", Registros.LeerOportunidad, tx, ("$id", duplicadoId));
            foreach (var x in oportunidades)
            {
                x.ClienteId = principalId;
                await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Oportunidad, x.Id, Registros.Datos(x));
            }
            var cotizaciones = await c.QueryAsync("SELECT id FROM cotizaciones WHERE cliente_id = $id", r => r.Guid("id"), tx, ("$id", duplicadoId));
            foreach (var id in cotizaciones)
            {
                var x = (await ServicioCotizaciones.ObtenerAsync(c, tx, id))!;
                x.ClienteId = principalId;
                await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, x.Id, Registros.Datos(x));
            }
            var tareas = await c.QueryAsync("SELECT * FROM tareas WHERE cliente_id = $id", Registros.LeerTarea, tx, ("$id", duplicadoId));
            foreach (var x in tareas)
            {
                x.ClienteId = principalId;
                await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Tarea, x.Id, Registros.Datos(x));
            }
            var leads = await c.QueryAsync("SELECT * FROM leads WHERE cliente_id = $id", Registros.LeerLead, tx, ("$id", duplicadoId));
            foreach (var x in leads)
            {
                x.ClienteId = principalId;
                await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Lead, x.Id, Registros.Datos(x));
            }
            // La actividad de la suite (ventas, comprobantes) es una vista local: aquí se ve junta en el principal.
            await c.ExecAsync("UPDATE actividad SET cliente_id = $principal WHERE cliente_id = $id", tx,
                ("$id", duplicadoId), ("$principal", principalId));

            await PublicarClienteAsync(c, tx, now, duplicadoId, body =>
            {
                body["isActive"] = false;
                var nota = T("cartera.nota_fusionado", principal.Name);
                body["notes"] = body["notes"]?.GetValue<string>() is { Length: > 0 } previa ? $"{previa}\n{nota}" : nota;
            });
            await c.ExecAsync("UPDATE parties SET is_active = 0 WHERE id = $id", tx, ("$id", duplicadoId));
            if (principal.OwnerId is null && duplicado.OwnerId is { } owner)
                await CambiarResponsableAsync(c, tx, now, principalId, owner, duplicado.OwnerName, auditar: false);
            await AuditAsync(c, tx, now, "customer.merged", "party", principalId, principal.DocumentNumber,
                $"{duplicado.Name} → {principal.Name}");
            return new ResultadoFusion(contactos.Count, oportunidades.Count, cotizaciones.Count, tareas.Count, leads.Count);
        }, ct);
    }

    // ---------- Internos ----------

    private static async Task<PartyRow> ClienteAsync(SqliteConnection c, SqliteTransaction tx, Guid id)
    {
        var cliente = await c.FirstOrDefaultAsync("SELECT * FROM parties WHERE id = $id AND is_deleted = 0", MasterDataService.ReadParty, tx, ("$id", id));
        Require(cliente is not null, T("error.el_cliente_no_existe"));
        return cliente!;
    }

    private async Task CambiarResponsableAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid clienteId,
        Guid? responsableId, string? responsableNombre, bool auditar = true)
    {
        var nombre = responsableId is null ? null : Clean(responsableNombre) ?? "?";
        await PublicarClienteAsync(c, tx, now, clienteId, body =>
            body["account"] = new JsonObject { ["ownerUserId"] = responsableId?.ToString(), ["ownerName"] = nombre });
        await c.ExecAsync("UPDATE parties SET owner_id = $owner, owner_name = $name WHERE id = $id", tx,
            ("$id", clienteId), ("$owner", responsableId), ("$name", nombre));
        if (auditar)
            await AuditAsync(c, tx, now, "customer.assigned", "party", clienteId, null, nombre ?? T("cartera.sin_responsable"));
    }

    /// <summary>Sube el cliente al hub tal como llegó, con el cambio indicado, sin <c>credit</c> (es del ERP).</summary>
    private static async Task PublicarClienteAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid clienteId,
        Action<JsonObject> cambio)
    {
        var hub = await c.ScalarAsync<string?>("SELECT hub_data FROM parties WHERE id = $id", tx, ("$id", clienteId));
        Require(hub is not null, T("error.el_cliente_aun_no_llega_del_hub"));
        var body = JsonNode.Parse(hub!)!.AsObject();
        foreach (var key in new[] { "id", "meta", "credit", "account" })
            body.Remove(key);
        cambio(body);
        await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Parties, clienteId, body);
        // La copia local sigue la versión pendiente, para que el siguiente cambio parta de esta.
        var copia = JsonNode.Parse(hub!)!.AsObject();
        foreach (var (key, value) in body)
            copia[key] = value?.DeepClone();
        await c.ExecAsync("UPDATE parties SET hub_data = $hub, pending = 1 WHERE id = $id", tx,
            ("$id", clienteId), ("$hub", copia.ToJsonString(VortexJson.Options)));
    }

    private static readonly string[] SufijosLegales = ["SAC", "SA", "SRL", "EIRL", "SAA", "SCRL", "SAS", "LTDA", "INC", "LLC"];

    /// <summary>Nombre para comparar: sin tildes, mayúsculas, puntuación ni forma societaria.</summary>
    public static string NombreNormalizado(string nombre)
    {
        var sinTildes = new string(nombre.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
        var palabras = new string(sinTildes.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == ' ' ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // "S.A.C." queda como "S A C": se juntan las letras sueltas antes de quitar la forma societaria.
        var juntas = new List<string>();
        foreach (var p in palabras)
        {
            if (p.Length == 1 && juntas.Count > 0 && juntas[^1].All(char.IsLetter) && juntas[^1].Length <= 4 && SufijosLegales.Any(s => s.StartsWith(juntas[^1] + p)))
                juntas[^1] += p;
            else
                juntas.Add(p);
        }
        while (juntas.Count > 1 && SufijosLegales.Contains(juntas[^1]))
            juntas.RemoveAt(juntas.Count - 1);
        return string.Join(' ', juntas);
    }
}
