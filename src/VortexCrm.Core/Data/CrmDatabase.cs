using Microsoft.Data.Sqlite;

namespace VortexCrm.Core.Data;

/// <summary>
/// Base local del CRM (SQLite, una por organización). Guarda la copia de los datos maestros del hub, los datos propios
/// del CRM (contactos, oportunidades, cotizaciones, tareas y ajustes), la actividad de los clientes que informan los
/// otros sistemas y la cola de sincronización, para que todo funcione sin conexión y cada cambio quede en la misma
/// transacción que su evento.
/// </summary>
public sealed class CrmDatabase
{
    public CrmDatabase(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }
    public string ConnectionString { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecAsync("PRAGMA foreign_keys = OFF; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    /// <summary>Crea o actualiza el esquema. Cada versión se aplica una sola vez (<c>PRAGMA user_version</c>).</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(ct);
        await connection.ExecAsync("PRAGMA journal_mode = WAL;");
        var current = await connection.ScalarAsync<long>("PRAGMA user_version;");
        for (var version = (int)current; version < Schema.Migrations.Length; version++)
        {
            await using var tx = connection.BeginTransaction();
            await connection.ExecAsync(Schema.Migrations[version], tx);
            await connection.ExecAsync($"PRAGMA user_version = {version + 1};", tx);
            await tx.CommitAsync(ct);
        }
    }
}

internal static class Schema
{
    /// <summary>Migraciones en orden. Nunca modificar una ya publicada: agregar una nueva al final.</summary>
    public static readonly string[] Migrations =
    [
        """
        -- Copia local de datos maestros del hub (dueño: hub). pending = cambio local aún no confirmado por el hub.
        CREATE TABLE items (
            id TEXT PRIMARY KEY, sku TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, description TEXT,
            unit TEXT NOT NULL, barcode TEXT, category TEXT, price REAL NOT NULL, currency_code TEXT NOT NULL,
            tax_code TEXT, track_inventory INTEGER NOT NULL, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE INDEX ix_items_sku ON items (sku);

        CREATE TABLE sites (
            id TEXT PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, phone TEXT,
            address_line TEXT, city TEXT, country_code TEXT, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);

        CREATE TABLE parties (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, trade_name TEXT, kind TEXT NOT NULL, document_type TEXT,
            document_number TEXT, is_customer INTEGER NOT NULL, is_supplier INTEGER NOT NULL, email TEXT, phone TEXT,
            address_line TEXT, district TEXT, city TEXT, region TEXT, notes TEXT, is_active INTEGER NOT NULL,
            version INTEGER NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT NOT NULL);
        CREATE INDEX ix_parties_document ON parties (document_number);

        CREATE TABLE company (
            id INTEGER PRIMARY KEY CHECK (id = 1), legal_name TEXT NOT NULL, trade_name TEXT, tax_id TEXT,
            country_code TEXT NOT NULL, currency_code TEXT NOT NULL, email TEXT, phone TEXT, address_line TEXT,
            city TEXT, version INTEGER NOT NULL);

        -- Datos propios del CRM (dueño: CRM). Se replican entre equipos con crm.record.upserted/deleted;
        -- actualizado_en decide qué versión gana.
        CREATE TABLE contactos (
            id TEXT PRIMARY KEY, cliente_id TEXT, nombres TEXT NOT NULL, apellidos TEXT NOT NULL, dni TEXT, telefono TEXT,
            email TEXT, cargo TEXT, creado_en TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        CREATE INDEX ix_contactos_cliente ON contactos (cliente_id);

        CREATE TABLE oportunidades (
            id TEXT PRIMARY KEY, titulo TEXT NOT NULL, cliente_id TEXT, contacto_id TEXT, monto REAL NOT NULL,
            etapa TEXT NOT NULL, fecha_cierre_estimada TEXT, motivo_perdida TEXT, notas TEXT, creado_en TEXT NOT NULL,
            cerrado_en TEXT, actualizado_en TEXT NOT NULL);
        CREATE INDEX ix_oportunidades_cliente ON oportunidades (cliente_id);

        CREATE TABLE cotizaciones (
            id TEXT PRIMARY KEY, numero TEXT, cliente_id TEXT, contacto_id TEXT, oportunidad_id TEXT, sede_id TEXT,
            fecha_emision TEXT NOT NULL, valida_hasta TEXT NOT NULL, modo_igv TEXT NOT NULL, condiciones TEXT,
            estado TEXT NOT NULL, pedido_id TEXT, creado_en TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        CREATE INDEX ix_cotizaciones_cliente ON cotizaciones (cliente_id);
        CREATE TABLE cotizacion_lineas (
            cotizacion_id TEXT NOT NULL, linea INTEGER NOT NULL, item_id TEXT, descripcion TEXT NOT NULL, unidad TEXT NOT NULL,
            cantidad REAL NOT NULL, precio_unitario REAL NOT NULL, PRIMARY KEY (cotizacion_id, linea));

        CREATE TABLE tareas (
            id TEXT PRIMARY KEY, titulo TEXT NOT NULL, tipo TEXT NOT NULL, fecha TEXT NOT NULL, hora TEXT,
            avisar_minutos_antes INTEGER, cliente_id TEXT, contacto_id TEXT, oportunidad_id TEXT, responsable_id TEXT,
            responsable_nombre TEXT, notas TEXT, creado_en TEXT NOT NULL, completada_en TEXT, actualizado_en TEXT NOT NULL);
        CREATE INDEX ix_tareas_fecha ON tareas (fecha);

        CREATE TABLE ajustes (
            id INTEGER PRIMARY KEY CHECK (id = 1), modo_igv TEXT NOT NULL, condiciones TEXT, actualizado_en TEXT NOT NULL);

        -- Lo que los otros sistemas informan de cada cliente (ventas, comprobantes, cobros, despachos).
        CREATE TABLE actividad (
            id TEXT PRIMARY KEY, cliente_id TEXT NOT NULL, tipo TEXT NOT NULL, titulo TEXT NOT NULL, detalle TEXT,
            monto REAL, moneda TEXT, origen TEXT NOT NULL, ocurrido_en TEXT NOT NULL);
        CREATE INDEX ix_actividad_cliente ON actividad (cliente_id, ocurrido_en);

        CREATE TABLE document_sequences (prefix TEXT PRIMARY KEY, value INTEGER NOT NULL);

        -- Integración: eventos ya aplicados (idempotencia) y estado de VortexSync (cursores, cola, rechazos).
        CREATE TABLE processed_events (
            event_id TEXT PRIMARY KEY, type TEXT NOT NULL, processed_at TEXT NOT NULL, note TEXT);
        CREATE TABLE sync_cursors (stream TEXT PRIMARY KEY, cursor INTEGER NOT NULL);
        CREATE TABLE sync_outbox (seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, entry TEXT NOT NULL);
        CREATE TABLE sync_failed (
            id TEXT PRIMARY KEY, entry TEXT NOT NULL, error_code TEXT NOT NULL, error TEXT NOT NULL, failed_at TEXT NOT NULL);
        """,
        // 2: base común de la suite. Impuesto y zona horaria de la empresa, moneda de la cotización, vendedor responsable de
        // oportunidades y cotizaciones, y bitácora de auditoría.
        """
        ALTER TABLE company ADD COLUMN default_tax_rate REAL;
        ALTER TABLE company ADD COLUMN time_zone TEXT;
        ALTER TABLE cotizaciones ADD COLUMN moneda TEXT NOT NULL DEFAULT 'PEN';
        ALTER TABLE cotizaciones ADD COLUMN responsable_id TEXT;
        ALTER TABLE cotizaciones ADD COLUMN responsable_nombre TEXT;
        ALTER TABLE oportunidades ADD COLUMN responsable_id TEXT;
        ALTER TABLE oportunidades ADD COLUMN responsable_nombre TEXT;
        """ + VortexApi.Suite.Auditing.AuditSchema.Sql,
    ];
}
