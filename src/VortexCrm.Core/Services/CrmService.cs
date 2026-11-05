using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexApi.Suite.Taxes;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>Base de los servicios: base local, contexto del usuario y reloj.</summary>
public abstract class CrmService(CrmDatabase db, ICrmContext context, TimeProvider clock)
{
    protected CrmDatabase Db { get; } = db;
    protected ICrmContext Context { get; } = context;
    protected TimeProvider Clock { get; } = clock;

    protected async Task<T> InTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, DateTimeOffset, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var result = await work(c, tx, Clock.GetUtcNow());
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Sede que emite las cotizaciones: la elegida en este equipo (null = domicilio fiscal).</summary>
    protected Guid? DefaultSite => Context.DefaultSiteId;

    /// <summary>Inicio del día local (Perú, UTC-5) como instante UTC, para filtrar "hoy".</summary>
    protected DateTimeOffset StartOfDay(int daysAgo = 0)
    {
        var local = Clock.GetUtcNow().ToOffset(CrmClock.Offset);
        return new DateTimeOffset(local.Date, CrmClock.Offset).AddDays(-daysAgo).ToUniversalTime();
    }

    protected static string Like(string? term) => $"%{term?.Trim().Replace("%", "").Replace("_", "")}%";

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Corta la operación si el rol del usuario no la permite (el mensaje se muestra tal cual).</summary>
    protected void RequirePermission(string permission)
    {
        if (!Context.Can(permission))
            throw new CrmValidationException(T("error.permiso", Context.Role, CrmPermissions.Describe(permission)));
    }

    /// <summary>Lo de otro vendedor solo lo toca quien puede editar todo (gerente o administrador).</summary>
    protected void RequireOwner(Guid? responsableId, string? responsableNombre)
    {
        if (responsableId is { } duenio && duenio != Context.UserId && !Context.Can(CrmPermissions.EditAny))
            throw new CrmValidationException(T("error.es_de_otro_vendedor", responsableNombre ?? T("error.otro_vendedor")));
    }

    /// <summary>Deja la acción en la bitácora, en la misma transacción que la acción.</summary>
    protected Task AuditAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string action, string entity, Guid? entityId,
        string? reference, string? detail) =>
        Audit.WriteAsync(c, tx, new AuditEntry(Guid.NewGuid(), now, Context.UserId, Context.UserName, Context.DeviceTag, action, entity,
            entityId?.ToString(), reference, detail));

    /// <summary>Régimen de impuesto y moneda de la empresa (del hub). Sin datos de la empresa, Perú y soles.</summary>
    internal static async Task<(TaxRegime Regimen, string Moneda)> FiscalAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        var company = await c.FirstOrDefaultAsync("SELECT country_code, currency_code, default_tax_rate FROM company WHERE id = 1",
            r => new { Country = r.Str("country_code"), Currency = r.Str("currency_code"), Rate = r.DecOrNull("default_tax_rate") }, tx);
        return (TaxCatalog.For(company?.Country ?? "PE", company?.Rate), company?.Currency ?? "PEN");
    }

    protected static void Require(bool condition, string message)
    {
        if (!condition)
            throw new CrmValidationException(message);
    }
}

/// <summary>Hora de Perú (UTC-5) para el día de hoy, vencimientos y reportes. Perú no usa horario de verano.</summary>
public static class CrmClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);
}

internal static class DocumentNumbers
{
    public const string Cotizacion = "COT";

    /// <summary>Numeración por equipo (<c>COT-3F0C-0012</c>): dos equipos sin conexión nunca generan el mismo número.</summary>
    public static async Task<string> NextAsync(SqliteConnection c, SqliteTransaction tx, string prefix, string deviceTag)
    {
        var value = await c.ScalarAsync<long>("""
            INSERT INTO document_sequences (prefix, value) VALUES ($p, 1)
            ON CONFLICT (prefix) DO UPDATE SET value = value + 1 RETURNING value
            """, tx, ("$p", prefix));
        return $"{prefix}-{deviceTag}-{value:D4}";
    }
}
/// <summary>Bitácora de acciones sensibles (no se edita ni se borra).</summary>
internal static class Audit
{
    public static Task WriteAsync(SqliteConnection c, SqliteTransaction tx, AuditEntry e) =>
        c.ExecAsync("""
            INSERT INTO audit_log (id, at, user_id, user_name, device, action, entity, entity_id, reference, detail)
            VALUES ($id, $at, $user, $name, $device, $action, $entity, $entityId, $ref, $detail)
            """, tx, ("$id", e.Id), ("$at", e.At), ("$user", e.UserId), ("$name", e.UserName), ("$device", e.Device),
            ("$action", e.Action), ("$entity", e.Entity), ("$entityId", e.EntityId), ("$ref", e.Reference), ("$detail", e.Detail));
}

/// <summary>Consulta de la bitácora.</summary>
public sealed class AuditService(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    public async Task<PagedResult<AuditRow>> GetAsync(int page, int pageSize, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.ViewAudit);
        await using var c = await Db.OpenAsync(ct);
        var total = await c.ScalarAsync<int>("SELECT COUNT(*) FROM audit_log");
        var rows = await c.QueryAsync("SELECT * FROM audit_log ORDER BY at DESC LIMIT $limit OFFSET $offset", r => new AuditRow(
            r.Guid("id"), r.Time("at"), r.StrOrNull("user_name"), r.StrOrNull("device"), r.Str("action"), r.Str("entity"),
            r.StrOrNull("reference"), r.StrOrNull("detail")), null, ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<AuditRow>(rows, total, page, pageSize);
    }
}
