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
        // 3: embudos configurables, competidor y motivos de pérdida, y metas de ventas. Embudos y metas son registros del
        // CRM: se replican con crm.record.upserted y gana el más nuevo.
        """
        ALTER TABLE oportunidades ADD COLUMN embudo_id TEXT;
        ALTER TABLE oportunidades ADD COLUMN etapa_id TEXT;
        ALTER TABLE oportunidades ADD COLUMN competidor TEXT;
        ALTER TABLE ajustes ADD COLUMN motivos_perdida TEXT;

        CREATE TABLE embudos (
            id TEXT PRIMARY KEY, nombre TEXT NOT NULL, etapas TEXT NOT NULL, por_defecto INTEGER NOT NULL DEFAULT 0,
            archivado INTEGER NOT NULL DEFAULT 0, actualizado_en TEXT NOT NULL);

        CREATE TABLE metas (
            id TEXT PRIMARY KEY, mes TEXT NOT NULL, responsable_id TEXT, responsable_nombre TEXT, monto REAL NOT NULL,
            actualizado_en TEXT NOT NULL);
        CREATE INDEX ix_metas_mes ON metas (mes);
        """,
        // 4: leads (interesados que aún no son clientes, con origen y conversión) y productos de la oportunidad. Ambos son
        // registros del CRM: se replican con crm.record.upserted y gana el más nuevo.
        """
        CREATE TABLE leads (
            id TEXT PRIMARY KEY, nombre TEXT NOT NULL, empresa TEXT, tipo_documento TEXT, documento TEXT, telefono TEXT, email TEXT,
            origen TEXT NOT NULL, estado TEXT NOT NULL, notas TEXT, motivo_descarte TEXT, responsable_id TEXT, responsable_nombre TEXT,
            cliente_id TEXT, contacto_id TEXT, oportunidad_id TEXT, creado_en TEXT NOT NULL, convertido_en TEXT,
            actualizado_en TEXT NOT NULL);
        CREATE INDEX ix_leads_estado ON leads (estado, creado_en);

        CREATE TABLE oportunidad_lineas (
            oportunidad_id TEXT NOT NULL, linea INTEGER NOT NULL, item_id TEXT, descripcion TEXT NOT NULL, cantidad REAL NOT NULL,
            precio_unitario REAL NOT NULL, PRIMARY KEY (oportunidad_id, linea));
        """,
        // 5: cartera (fase 2c). Responsable del cliente (bloque account del party del hub) y una copia del cliente tal como
        // llega del hub, para devolverlo completo al cambiar el responsable o al desactivar un duplicado. Se vuelven a
        // bajar los clientes para traer su responsable.
        """
        ALTER TABLE parties ADD COLUMN owner_id TEXT;
        ALTER TABLE parties ADD COLUMN owner_name TEXT;
        ALTER TABLE parties ADD COLUMN hub_data TEXT;
        CREATE INDEX ix_parties_owner ON parties (owner_id);
        DELETE FROM sync_cursors WHERE stream = 'master';
        """,
        // 6: actividades (fase 3). Interacciones con el cliente (llamadas, WhatsApp, correos, reuniones, visitas) y
        // plantillas de mensajes. Ambas son registros del CRM (crm.record.upserted).
        """
        CREATE TABLE interacciones (
            id TEXT PRIMARY KEY, cliente_id TEXT, contacto_id TEXT, oportunidad_id TEXT, tarea_id TEXT, tipo TEXT NOT NULL,
            fecha TEXT NOT NULL, resumen TEXT NOT NULL, resultado TEXT, responsable_id TEXT, responsable_nombre TEXT,
            creado_en TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        CREATE INDEX ix_interacciones_cliente ON interacciones (cliente_id, fecha);
        CREATE TABLE plantillas (
            id TEXT PRIMARY KEY, nombre TEXT NOT NULL, canal TEXT NOT NULL, asunto TEXT, cuerpo TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        """,
        // 7: cotizaciones empresariales (fase 4a). Versiones y aprobación del descuento de cada cotización; precio de lista y
        // líneas opcionales; plantillas de cotización (registro del CRM); descuento máximo del vendedor en los ajustes; copia de
        // las listas de precios del hub (price_list) y la lista de cada cliente (credit.priceListId, se vuelve a leer el feed).
        """
        ALTER TABLE cotizaciones ADD COLUMN version INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE cotizaciones ADD COLUMN version_de TEXT;
        ALTER TABLE cotizaciones ADD COLUMN aprobada_por TEXT;
        ALTER TABLE cotizaciones ADD COLUMN aprobada_en TEXT;
        ALTER TABLE cotizaciones ADD COLUMN descuento_aprobado REAL;
        ALTER TABLE cotizacion_lineas ADD COLUMN precio_lista REAL;
        ALTER TABLE cotizacion_lineas ADD COLUMN opcional INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE ajustes ADD COLUMN descuento_maximo REAL;
        CREATE TABLE plantillas_cotizacion (id TEXT PRIMARY KEY, nombre TEXT NOT NULL, datos TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        CREATE TABLE price_lists (
            id TEXT PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, currency_code TEXT NOT NULL, valid_from TEXT, valid_to TEXT,
            site_ids TEXT NOT NULL DEFAULT '[]', is_active INTEGER NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0,
            version INTEGER NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE price_list_lines (
            list_id TEXT NOT NULL, item_id TEXT NOT NULL, min_qty REAL NOT NULL, price REAL NOT NULL, PRIMARY KEY (list_id, item_id, min_qty));
        ALTER TABLE parties ADD COLUMN price_list_id TEXT;
        DELETE FROM sync_cursors WHERE stream = 'master';
        """,
        // 8: aceptación en línea (fase 4b). Enlace público del hub y la respuesta del cliente (firmante, fecha, comentario).
        """
        ALTER TABLE cotizaciones ADD COLUMN enlace_id TEXT;
        ALTER TABLE cotizaciones ADD COLUMN enlace_url TEXT;
        ALTER TABLE cotizaciones ADD COLUMN firmada_por TEXT;
        ALTER TABLE cotizaciones ADD COLUMN firmada_correo TEXT;
        ALTER TABLE cotizaciones ADD COLUMN firmada_en TEXT;
        ALTER TABLE cotizaciones ADD COLUMN respuesta_cliente TEXT;
        """,
        // 9: automatización (fase 5a). Reglas, secuencias y la asignación de leads como JSON; disparos (qué regla ya saltó
        // para qué registro, para que no se repita en otro equipo); origen de las tareas automáticas.
        """
        CREATE TABLE reglas (id TEXT PRIMARY KEY, datos TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        CREATE TABLE secuencias (id TEXT PRIMARY KEY, datos TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        CREATE TABLE configuracion (id TEXT PRIMARY KEY, datos TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        CREATE TABLE disparos (
            id TEXT PRIMARY KEY, regla_id TEXT NOT NULL, registro_id TEXT NOT NULL, creado_en TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        ALTER TABLE tareas ADD COLUMN origen TEXT;
        """,
        // 10: equipos de venta (fase 5b). Equipo con líder, vendedores, territorio y equipo padre, como JSON; la visibilidad y
        // el plan de comisiones van en configuracion.
        """
        CREATE TABLE equipos (id TEXT PRIMARY KEY, datos TEXT NOT NULL, actualizado_en TEXT NOT NULL);
        """,
    ];
}
