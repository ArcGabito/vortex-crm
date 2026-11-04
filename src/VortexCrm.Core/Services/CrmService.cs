using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;

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
