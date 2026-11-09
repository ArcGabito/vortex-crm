using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Leads: interesados que todavía no son clientes, con su origen. Se replican como <c>crm.record.upserted</c> (kind
/// <c>lead</c>). Convertir uno crea (o reusa por documento) el cliente, su contacto y, si se pide, una oportunidad.
/// </summary>
public sealed class ServicioLeads(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    public async Task<IReadOnlyList<Lead>> ListarAsync(string? busqueda = null, string? estado = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var visibles = await ServicioEquipos.VisiblesAsync(c, null, Context);
        return await c.QueryAsync("""
            SELECT * FROM leads
            WHERE ($estado IS NULL OR estado = $estado)
              AND ($vis IS NULL OR responsable_id IS NULL OR responsable_id IN (SELECT value FROM json_each($vis)))
              AND ($q IS NULL OR nombre LIKE $like OR empresa LIKE $like OR documento LIKE $like OR email LIKE $like OR telefono LIKE $like)
            ORDER BY CASE estado WHEN 'nuevo' THEN 0 WHEN 'contactado' THEN 1 WHEN 'calificado' THEN 2 ELSE 3 END, creado_en DESC
            """, Registros.LeerLead, null, ("$estado", Clean(estado)), ("$q", Clean(busqueda)), ("$like", Like(busqueda)), ("$vis", visibles));
    }

    public async Task<Lead?> ObtenerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ObtenerAsync(c, null, id);
    }

    /// <summary>Cuántos leads hay en cada estado (para las pestañas y el panel).</summary>
    public async Task<IReadOnlyDictionary<string, int>> ContarAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var filas = await c.QueryAsync("SELECT estado, COUNT(*) AS n FROM leads GROUP BY estado",
            r => (Estado: r.Str("estado"), N: r.Int("n")));
        return filas.ToDictionary(f => f.Estado, f => f.N);
    }

    /// <summary>El cliente que ya tiene el documento del lead (para avisar antes de convertir y no duplicarlo).</summary>
    public async Task<Option?> ClienteConDocumentoAsync(string? documento, CancellationToken ct = default)
    {
        if (Clean(documento) is not { } doc)
            return null;
        await using var c = await Db.OpenAsync(ct);
        return await ClienteConDocumentoAsync(c, null, doc);
    }

    public Task GuardarAsync(Lead lead, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        var errores = lead.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var anterior = await ObtenerAsync(c, tx, lead.Id);
            if (anterior is not null)
            {
                RequireOwner(anterior.ResponsableId, anterior.ResponsableNombre);
                Require(anterior.Estado != EstadosLead.Convertido, T("error.el_lead_ya_se_convirtio"));
                // El estado y lo de la conversión solo los cambia este servicio.
                lead.CambiarEstado(anterior.Estado, anterior.MotivoDescarte);
            }
            if (anterior is null)
                lead.CreadoEn = now;
            else
                lead.CreadoEn = anterior.CreadoEn;
            // Un lead nuevo sin responsable se reparte según la asignación automática (CRM-12); si no hay, queda para quien lo registra.
            if (anterior is null && lead.ResponsableId is null && await ServicioAutomatizacion.ElegirVendedorAsync(c, tx) is { } vendedor)
                (lead.ResponsableId, lead.ResponsableNombre) = (vendedor.Id, vendedor.Nombre);
            lead.ResponsableId ??= Context.UserId;
            lead.ResponsableNombre ??= Context.UserName;
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Lead, lead.Id, Registros.Datos(lead));
            return 0;
        }, ct);
    }

    /// <summary>Contactado, calificado, descartado (con motivo) o de vuelta a nuevo.</summary>
    public Task CambiarEstadoAsync(Guid id, string estado, string? motivo = null, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        Require(estado is EstadosLead.Nuevo or EstadosLead.Contactado or EstadosLead.Calificado or EstadosLead.Descartado,
            T("error.estado_del_lead_desconocido"));
        Require(estado != EstadosLead.Descartado || !string.IsNullOrWhiteSpace(motivo), T("error.escribe_por_que_se_descarta"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var lead = await ObtenerAsync(c, tx, id);
            Require(lead is not null, T("error.el_lead_no_existe"));
            RequireOwner(lead!.ResponsableId, lead.ResponsableNombre);
            Require(lead.Estado != EstadosLead.Convertido, T("error.el_lead_ya_se_convirtio"));
            lead.CambiarEstado(estado, motivo);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Lead, lead.Id, Registros.Datos(lead));
            return 0;
        }, ct);
    }

    /// <summary>
    /// Convierte el lead: usa el cliente indicado o el que ya tiene su documento, o crea uno (empresa con RUC o persona);
    /// crea el contacto con sus datos y, si se pide, una oportunidad en el embudo elegido. Todo en una transacción.
    /// </summary>
    public Task<ResultadoConversion> ConvertirAsync(Guid id, ConversionLead opciones, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        Require(opciones.Monto >= 0, T("error.el_monto_no_puede_ser_negativo"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var lead = await ObtenerAsync(c, tx, id);
            Require(lead is not null, T("error.el_lead_no_existe"));
            RequireOwner(lead!.ResponsableId, lead.ResponsableNombre);
            Require(lead.EstaAbierto, lead.Estado == EstadosLead.Convertido ? T("error.el_lead_ya_se_convirtio") : T("error.el_lead_esta_descartado"));

            // Cliente: el elegido, el que ya tiene ese documento o uno nuevo.
            var clienteId = opciones.ClienteId;
            if (clienteId is { } elegido)
                Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties WHERE id = $id AND is_deleted = 0", tx, ("$id", elegido)) > 0,
                    T("error.el_cliente_no_existe"));
            clienteId ??= lead.Documento is { } doc ? (await ClienteConDocumentoAsync(c, tx, doc))?.Id : null;
            var nuevo = clienteId is null;
            if (nuevo)
            {
                RequirePermission(CrmPermissions.CreateCustomer);
                var empresa = !string.IsNullOrWhiteSpace(lead.Empresa) || lead.TipoDocumento == "RUC";
                clienteId = await MasterDataService.InsertPartyAsync(c, tx, now, new PartyDraft(
                    lead.Titulo, empresa ? "Company" : "Person", IsCustomer: true, IsSupplier: false, lead.TipoDocumento, lead.Documento,
                    lead.Email, lead.Telefono, null, Notes: T("info.convertido_de_lead", OrigenNombre(lead.Origen))));
            }

            // Contacto con los datos de la persona.
            var partes = lead.Nombre.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var contacto = new Contacto
            {
                ClienteId = clienteId, Nombres = partes[0], Apellidos = partes.Length > 1 ? partes[1] : "",
                Dni = lead.TipoDocumento == "DNI" ? lead.Documento : null, Telefono = lead.Telefono, Email = lead.Email, CreadoEn = now,
            };
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Contacto, contacto.Id, Registros.Datos(contacto));

            Guid? oportunidadId = null;
            if (opciones.CrearOportunidad)
            {
                var oportunidad = new Oportunidad
                {
                    Titulo = string.IsNullOrWhiteSpace(opciones.TituloOportunidad) ? lead.Titulo : opciones.TituloOportunidad.Trim(),
                    ClienteId = clienteId, ContactoId = contacto.Id, Monto = opciones.Monto, CreadoEn = now,
                    ResponsableId = lead.ResponsableId ?? Context.UserId, ResponsableNombre = lead.ResponsableNombre ?? Context.UserName,
                    EmbudoId = opciones.EmbudoId ?? await ServicioEmbudos.EmbudoPorDefectoAsync(c, tx),
                    Notas = lead.Notas,
                };
                var embudo = await ServicioEmbudos.EmbudoAsync(c, tx, oportunidad.EmbudoId);
                Require(oportunidad.EmbudoId is null || !embudo.EsPredeterminado, T("error.el_embudo_no_existe"));
                await ServicioOportunidades.GuardarAsync(c, tx, now, oportunidad, null);
                oportunidadId = oportunidad.Id;
            }

            lead.MarcarConvertido(clienteId!.Value, contacto.Id, oportunidadId, now);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Lead, lead.Id, Registros.Datos(lead));
            await AuditAsync(c, tx, now, "lead.converted", "lead", lead.Id, lead.Titulo,
                T(nuevo ? "info.lead_convertido_cliente_nuevo" : "info.lead_convertido_cliente_existente"));
            return new ResultadoConversion(clienteId.Value, nuevo, contacto.Id, oportunidadId);
        }, ct);
    }

    public Task EliminarAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Delete);
        return InTransactionAsync(async (c, tx, now) =>
        {
            Require(await ObtenerAsync(c, tx, id) is not null, T("error.el_lead_no_existe"));
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Lead, id);
            return 0;
        }, ct);
    }

    // ---------- Internos ----------

    private static Task<Lead?> ObtenerAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync("SELECT * FROM leads WHERE id = $id", Registros.LeerLead, tx, ("$id", id));

    private static Task<Option?> ClienteConDocumentoAsync(SqliteConnection c, SqliteTransaction? tx, string documento) =>
        c.FirstOrDefaultAsync("SELECT id, name, document_number FROM parties WHERE document_number = $doc AND is_deleted = 0 LIMIT 1",
            r => new Option(r.Guid("id"), r.Str("name"), r.StrOrNull("document_number")), tx, ("$doc", documento.Trim()));

    private static string OrigenNombre(string origen) => T($"lead.origen.{origen}");
}
