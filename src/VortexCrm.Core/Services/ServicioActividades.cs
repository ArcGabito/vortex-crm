using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Actividades;
using VortexCrm.Core.Integration;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Actividades con los clientes (fase 3): interacciones (llamadas, WhatsApp, correos, reuniones, visitas, notas) y
/// plantillas de mensajes. Ambas se replican a todo el equipo como registros del CRM.
/// </summary>
public sealed class ServicioActividades(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    // ---------- Interacciones ----------

    public async Task<IReadOnlyList<InteraccionVista>> InteraccionesAsync(Guid? clienteId = null, Guid? oportunidadId = null, int limite = 50,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT i.*, p.name AS cliente, TRIM(k.nombres || ' ' || k.apellidos) AS contacto
            FROM interacciones i LEFT JOIN parties p ON p.id = i.cliente_id LEFT JOIN contactos k ON k.id = i.contacto_id
            WHERE ($cliente IS NULL OR i.cliente_id = $cliente) AND ($oportunidad IS NULL OR i.oportunidad_id = $oportunidad)
            ORDER BY i.fecha DESC LIMIT $limite
            """, r => new InteraccionVista(Registros.LeerInteraccion(r), r.StrOrNull("cliente"), r.StrOrNull("contacto")), null,
            ("$cliente", clienteId), ("$oportunidad", oportunidadId), ("$limite", limite));
    }

    /// <summary>Nueva interacción del usuario, con la fecha de ahora si no tiene.</summary>
    public Interaccion Nueva(Guid? clienteId, TipoInteraccion tipo = TipoInteraccion.Llamada) => new()
    {
        ClienteId = clienteId, Tipo = tipo, Fecha = Clock.GetUtcNow(), ResponsableId = Context.UserId, ResponsableNombre = Context.UserName,
        CreadoEn = Clock.GetUtcNow(),
    };

    /// <summary>Registra (o corrige) una interacción. Cada vendedor edita las suyas; el gerente, cualquiera.</summary>
    public Task RegistrarAsync(Interaccion interaccion, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        Require(!string.IsNullOrWhiteSpace(interaccion.Resumen), T("error.escribe_que_paso"));
        Require(interaccion.ClienteId is not null || interaccion.OportunidadId is not null, T("error.la_interaccion_necesita_cliente"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            if (await c.FirstOrDefaultAsync("SELECT * FROM interacciones WHERE id = $id", Registros.LeerInteraccion, tx, ("$id", interaccion.Id))
                is { } guardada)
                RequireOwner(guardada.ResponsableId, guardada.ResponsableNombre);
            if (interaccion.CreadoEn == default)
                interaccion.CreadoEn = now;
            if (interaccion.Fecha == default)
                interaccion.Fecha = now;
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Interaccion, interaccion.Id, Registros.Datos(interaccion));
            return 0;
        }, ct);
    }

    public Task EliminarInteraccionAsync(Guid id, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var guardada = await c.FirstOrDefaultAsync("SELECT * FROM interacciones WHERE id = $id", Registros.LeerInteraccion, tx, ("$id", id));
            Require(guardada is not null, T("error.la_interaccion_no_existe"));
            if (guardada!.ResponsableId is { } duenio && duenio != Context.UserId)
                RequirePermission(CrmPermissions.Delete);
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Interaccion, id);
            return 0;
        }, ct);

    // ---------- Plantillas ----------

    public async Task<IReadOnlyList<Plantilla>> PlantillasAsync(CanalPlantilla? canal = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM plantillas WHERE ($canal IS NULL OR canal = $canal) ORDER BY nombre COLLATE NOCASE",
            Registros.LeerPlantilla, null, ("$canal", canal?.ToString()));
    }

    /// <summary>Crea o cambia una plantilla (las configura quien puede editar los ajustes de ventas).</summary>
    public Task GuardarPlantillaAsync(Plantilla plantilla, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        Require(!string.IsNullOrWhiteSpace(plantilla.Nombre), T("error.el_nombre_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(plantilla.Cuerpo), T("error.la_plantilla_no_tiene_texto"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Plantilla, plantilla.Id, Registros.Datos(plantilla));
            return 0;
        }, ct);
    }

    public Task EliminarPlantillaAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Plantilla, id);
            return 0;
        }, ct);
    }

    /// <summary>Datos para llenar una plantilla para un cliente (y su contacto principal), con el usuario y la empresa.</summary>
    public async Task<DatosPlantilla> DatosAsync(Guid? clienteId, Guid? contactoId = null, string? cotizacion = null, string? monto = null,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var cliente = clienteId is { } id ? await c.ScalarAsync<string?>("SELECT COALESCE(trade_name, name) FROM parties WHERE id = $id", null, ("$id", id)) : null;
        var contacto = await c.ScalarAsync<string?>("""
            SELECT nombres FROM contactos WHERE ($contacto IS NOT NULL AND id = $contacto) OR ($contacto IS NULL AND cliente_id = $cliente)
            ORDER BY creado_en LIMIT 1
            """, null, ("$contacto", contactoId), ("$cliente", clienteId));
        var empresa = await c.ScalarAsync<string?>("SELECT COALESCE(trade_name, legal_name) FROM company WHERE id = 1");
        return new DatosPlantilla(cliente, contacto, Context.UserName, empresa, cotizacion, monto,
            Domain.Comun.FormatoPeru.Hoy(Clock).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture));
    }
}
