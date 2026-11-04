using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Integration;

namespace VortexCrm.Core.Services;

/// <summary>Personas de contacto en los clientes. Son del CRM y se replican a los demás equipos de la organización.</summary>
public sealed class ServicioContactos(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    public async Task<IReadOnlyList<ContactoVista>> ListarAsync(string? busqueda = null, Guid? clienteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT k.*, p.name AS cliente FROM contactos k LEFT JOIN parties p ON p.id = k.cliente_id
            WHERE ($cliente IS NULL OR k.cliente_id = $cliente)
              AND ($q IS NULL OR k.nombres LIKE $like OR k.apellidos LIKE $like OR k.email LIKE $like OR k.telefono LIKE $like
                   OR k.dni = $q OR p.name LIKE $like)
            ORDER BY k.nombres COLLATE NOCASE, k.apellidos COLLATE NOCASE
            """, r => new ContactoVista(Registros.LeerContacto(r), r.StrOrNull("cliente")), null,
            ("$cliente", clienteId), ("$q", Clean(busqueda)), ("$like", Like(busqueda)));
    }

    public async Task<Contacto?> ObtenerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("SELECT * FROM contactos WHERE id = $id", Registros.LeerContacto, null, ("$id", id));
    }

    public async Task<IReadOnlyList<Option>> OpcionesAsync(Guid? clienteId, CancellationToken ct = default) =>
        (await ListarAsync(null, clienteId, ct)).Select(v => new Option(v.Contacto.Id, v.Contacto.NombreCompleto, v.Contacto.Cargo)).ToList();

    public Task GuardarAsync(Contacto contacto, CancellationToken ct = default)
    {
        var errores = contacto.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Contacto, contacto.Id, Registros.Datos(contacto));
            return 0;
        }, ct);
    }

    public Task EliminarAsync(Guid id, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Contacto, id);
            return 0;
        }, ct);
}
