using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Tareas;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Sync;

namespace VortexCrm.Core.Data;

/// <summary>
/// Guarda y lee los registros propios del CRM. Un mismo camino para lo que se edita en este equipo (que además se
/// publica como <c>crm.record.upserted</c>) y para lo que llega de otro equipo (que se aplica solo si es más nuevo).
/// </summary>
internal static class Registros
{
    // ---------- Formato de los datos que viajan en crm.record.upserted ----------

    public sealed record ContactoDatos(
        Guid Id, Guid? ClienteId, string Nombres, string Apellidos, string? Dni, string? Telefono, string? Email, string? Cargo,
        DateTimeOffset CreadoEn);

    public sealed record OportunidadDatos(
        Guid Id, string Titulo, Guid? ClienteId, Guid? ContactoId, decimal Monto, string Etapa, DateOnly? FechaCierreEstimada,
        string? MotivoPerdida, string? Notas, DateTimeOffset CreadoEn, DateTimeOffset? CerradoEn, Guid? ResponsableId = null,
        string? ResponsableNombre = null);

    public sealed record LineaDatos(Guid? ItemId, string Descripcion, string Unidad, decimal Cantidad, decimal PrecioUnitario);

    public sealed record CotizacionDatos(
        Guid Id, string? Numero, Guid? ClienteId, Guid? ContactoId, Guid? OportunidadId, Guid? SedeId, DateOnly FechaEmision,
        DateOnly ValidaHasta, string ModoIgv, string? Condiciones, string Estado, Guid? PedidoId, DateTimeOffset CreadoEn,
        IReadOnlyList<LineaDatos> Lineas, string Moneda = "PEN", Guid? ResponsableId = null, string? ResponsableNombre = null);

    public sealed record TareaDatos(
        Guid Id, string Titulo, string Tipo, DateOnly Fecha, TimeOnly? Hora, int? AvisarMinutosAntes, Guid? ClienteId,
        Guid? ContactoId, Guid? OportunidadId, Guid? ResponsableId, string? ResponsableNombre, string? Notas,
        DateTimeOffset CreadoEn, DateTimeOffset? CompletadaEn);

    public sealed record AjustesDatos(string ModoIgv, string? Condiciones);

    // ---------- Publicar ----------

    /// <summary>Guarda el registro (gana siempre: es el cambio más reciente en este equipo) y encola su réplica.</summary>
    public static async Task PublicarAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, Guid id, object datos)
    {
        var data = JsonSerializer.SerializeToElement(datos, VortexJson.Options);
        await AplicarAsync(c, tx, kind, data, now, local: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new CrmRecord(kind, id, now, data), subjectType: kind,
            subjectId: id.ToString());
    }

    public static async Task EliminarAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, Guid id)
    {
        await BorrarAsync(c, tx, kind, id, null);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordDeleted, new CrmRecord(kind, id, now), subjectType: kind,
            subjectId: id.ToString());
    }

    // ---------- Aplicar (local o de otro equipo) ----------

    /// <summary>
    /// Escribe el registro. Lo local siempre gana; lo remoto solo si es más nuevo que lo guardado (el último cambio gana
    /// y repetir un evento no cambia nada). Devuelve false si no se aplicó.
    /// </summary>
    public static async Task<bool> AplicarAsync(SqliteConnection c, SqliteTransaction tx, string kind, JsonElement data,
        DateTimeOffset actualizado, bool local)
    {
        var tabla = Tabla(kind);
        if (!local && tabla is not null)
        {
            object id = kind == CrmRecordKinds.Ajustes ? 1 : data.GetProperty("id").GetGuid();
            var actual = await c.ScalarAsync<string?>($"SELECT actualizado_en FROM {tabla} WHERE id = $id", tx, ("$id", id));
            if (actual is not null && string.CompareOrdinal(actual, SqliteExtensions.Time(actualizado)) >= 0)
                return false;
        }

        switch (kind)
        {
            case CrmRecordKinds.Contacto:
                var ct = data.Deserialize<ContactoDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO contactos (id, cliente_id, nombres, apellidos, dni, telefono, email, cargo, creado_en, actualizado_en)
                    VALUES ($id, $cliente, $nombres, $apellidos, $dni, $telefono, $email, $cargo, $creado, $actualizado)
                    """, tx, ("$id", ct.Id), ("$cliente", ct.ClienteId), ("$nombres", ct.Nombres), ("$apellidos", ct.Apellidos), ("$dni", ct.Dni),
                    ("$telefono", ct.Telefono), ("$email", ct.Email), ("$cargo", ct.Cargo), ("$creado", ct.CreadoEn), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Oportunidad:
                var op = data.Deserialize<OportunidadDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO oportunidades (id, titulo, cliente_id, contacto_id, monto, etapa, fecha_cierre_estimada, motivo_perdida,
                                                          notas, creado_en, cerrado_en, responsable_id, responsable_nombre, actualizado_en)
                    VALUES ($id, $titulo, $cliente, $contacto, $monto, $etapa, $cierre, $motivo, $notas, $creado, $cerrado, $responsable,
                            $responsableNombre, $actualizado)
                    """, tx, ("$id", op.Id), ("$titulo", op.Titulo), ("$cliente", op.ClienteId), ("$contacto", op.ContactoId), ("$monto", op.Monto),
                    ("$etapa", op.Etapa), ("$cierre", Fecha(op.FechaCierreEstimada)), ("$motivo", op.MotivoPerdida), ("$notas", op.Notas),
                    ("$creado", op.CreadoEn), ("$cerrado", op.CerradoEn), ("$responsable", op.ResponsableId),
                    ("$responsableNombre", op.ResponsableNombre), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Cotizacion:
                var co = data.Deserialize<CotizacionDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO cotizaciones (id, numero, cliente_id, contacto_id, oportunidad_id, sede_id, fecha_emision, valida_hasta,
                                                         modo_igv, condiciones, estado, pedido_id, creado_en, moneda, responsable_id,
                                                         responsable_nombre, actualizado_en)
                    VALUES ($id, $numero, $cliente, $contacto, $oportunidad, $sede, $emision, $valida, $modo, $condiciones, $estado, $pedido,
                            $creado, $moneda, $responsable, $responsableNombre, $actualizado)
                    """, tx, ("$id", co.Id), ("$numero", co.Numero), ("$cliente", co.ClienteId), ("$contacto", co.ContactoId),
                    ("$oportunidad", co.OportunidadId), ("$sede", co.SedeId), ("$emision", Fecha(co.FechaEmision)), ("$valida", Fecha(co.ValidaHasta)),
                    ("$modo", co.ModoIgv), ("$condiciones", co.Condiciones), ("$estado", co.Estado), ("$pedido", co.PedidoId),
                    ("$creado", co.CreadoEn), ("$moneda", co.Moneda ?? "PEN"), ("$responsable", co.ResponsableId),
                    ("$responsableNombre", co.ResponsableNombre), ("$actualizado", actualizado));
                await c.ExecAsync("DELETE FROM cotizacion_lineas WHERE cotizacion_id = $id", tx, ("$id", co.Id));
                var n = 0;
                foreach (var l in co.Lineas ?? [])
                {
                    await c.ExecAsync("""
                        INSERT INTO cotizacion_lineas (cotizacion_id, linea, item_id, descripcion, unidad, cantidad, precio_unitario)
                        VALUES ($id, $n, $item, $descripcion, $unidad, $cantidad, $precio)
                        """, tx, ("$id", co.Id), ("$n", ++n), ("$item", l.ItemId), ("$descripcion", l.Descripcion), ("$unidad", l.Unidad),
                        ("$cantidad", l.Cantidad), ("$precio", l.PrecioUnitario));
                }
                return true;

            case CrmRecordKinds.Tarea:
                var t = data.Deserialize<TareaDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO tareas (id, titulo, tipo, fecha, hora, avisar_minutos_antes, cliente_id, contacto_id, oportunidad_id,
                                                   responsable_id, responsable_nombre, notas, creado_en, completada_en, actualizado_en)
                    VALUES ($id, $titulo, $tipo, $fecha, $hora, $avisar, $cliente, $contacto, $oportunidad, $responsable, $responsableNombre,
                            $notas, $creado, $completada, $actualizado)
                    """, tx, ("$id", t.Id), ("$titulo", t.Titulo), ("$tipo", t.Tipo), ("$fecha", Fecha(t.Fecha)), ("$hora", t.Hora?.ToString("HH:mm", CultureInfo.InvariantCulture)),
                    ("$avisar", t.AvisarMinutosAntes), ("$cliente", t.ClienteId), ("$contacto", t.ContactoId), ("$oportunidad", t.OportunidadId),
                    ("$responsable", t.ResponsableId), ("$responsableNombre", t.ResponsableNombre), ("$notas", t.Notas), ("$creado", t.CreadoEn),
                    ("$completada", t.CompletadaEn), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Ajustes:
                var a = data.Deserialize<AjustesDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO ajustes (id, modo_igv, condiciones, actualizado_en) VALUES (1, $modo, $condiciones, $actualizado)
                    """, tx, ("$modo", a.ModoIgv), ("$condiciones", a.Condiciones), ("$actualizado", actualizado));
                return true;

            default:
                return false;
        }
    }

    /// <summary>Borra el registro; si viene de otro equipo, solo si no se editó aquí después.</summary>
    public static async Task<bool> BorrarAsync(SqliteConnection c, SqliteTransaction tx, string kind, Guid id, DateTimeOffset? borradoEn)
    {
        if (Tabla(kind) is not { } tabla || kind == CrmRecordKinds.Ajustes)
            return false;
        var condicion = borradoEn is null ? "" : " AND actualizado_en <= $en";
        var borrados = await c.ExecAsync($"DELETE FROM {tabla} WHERE id = $id{condicion}", tx, ("$id", id), ("$en", borradoEn));
        if (kind == CrmRecordKinds.Cotizacion && borrados > 0)
            await c.ExecAsync("DELETE FROM cotizacion_lineas WHERE cotizacion_id = $id", tx, ("$id", id));
        return borrados > 0;
    }

    private static string? Tabla(string kind) => kind switch
    {
        CrmRecordKinds.Contacto => "contactos",
        CrmRecordKinds.Oportunidad => "oportunidades",
        CrmRecordKinds.Cotizacion => "cotizaciones",
        CrmRecordKinds.Tarea => "tareas",
        CrmRecordKinds.Ajustes => "ajustes",
        _ => null,
    };

    // ---------- Del dominio a los datos y de vuelta ----------

    public static ContactoDatos Datos(Contacto c) =>
        new(c.Id, c.ClienteId, c.Nombres.Trim(), c.Apellidos.Trim(), Limpio(c.Dni), Limpio(c.Telefono), Limpio(c.Email), Limpio(c.Cargo), c.CreadoEn);

    public static OportunidadDatos Datos(Oportunidad o) =>
        new(o.Id, o.Titulo.Trim(), o.ClienteId, o.ContactoId, o.Monto, o.Etapa.ToString(), o.FechaCierreEstimada, Limpio(o.MotivoPerdida),
            Limpio(o.Notas), o.CreadoEn, o.CerradoEn, o.ResponsableId, o.ResponsableNombre);

    public static CotizacionDatos Datos(Cotizacion c) =>
        new(c.Id, c.Numero, c.ClienteId, c.ContactoId, c.OportunidadId, c.SedeId, c.FechaEmision, c.ValidaHasta, c.ModoIgv.ToString(),
            Limpio(c.Condiciones), c.Estado.ToString(), c.PedidoId, c.CreadoEn,
            c.Lineas.Where(l => !l.EstaVacia).Select(l => new LineaDatos(l.ItemId, l.Descripcion.Trim(), l.Unidad, l.Cantidad, l.PrecioUnitario)).ToList(),
            c.Moneda, c.ResponsableId, c.ResponsableNombre);

    public static TareaDatos Datos(Tarea t) =>
        new(t.Id, t.Titulo.Trim(), t.Tipo.ToString(), t.Fecha, t.Hora, t.AvisarMinutosAntes, t.ClienteId, t.ContactoId, t.OportunidadId,
            t.ResponsableId, t.ResponsableNombre, Limpio(t.Notas), t.CreadoEn, t.CompletadaEn);

    public static Contacto LeerContacto(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), ClienteId = r.GuidOrNull("cliente_id"), Nombres = r.Str("nombres"), Apellidos = r.Str("apellidos"),
        Dni = r.StrOrNull("dni"), Telefono = r.StrOrNull("telefono"), Email = r.StrOrNull("email"), Cargo = r.StrOrNull("cargo"),
        CreadoEn = r.Time("creado_en"),
    };

    public static Oportunidad LeerOportunidad(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), Titulo = r.Str("titulo"), ClienteId = r.GuidOrNull("cliente_id"), ContactoId = r.GuidOrNull("contacto_id"),
        Monto = r.Dec("monto"), Etapa = Enum.Parse<EtapaOportunidad>(r.Str("etapa")), FechaCierreEstimada = FechaOrNull(r, "fecha_cierre_estimada"),
        MotivoPerdida = r.StrOrNull("motivo_perdida"), Notas = r.StrOrNull("notas"), CreadoEn = r.Time("creado_en"),
        CerradoEn = r.TimeOrNull("cerrado_en"), ResponsableId = r.GuidOrNull("responsable_id"), ResponsableNombre = r.StrOrNull("responsable_nombre"),
    };

    public static Cotizacion LeerCotizacion(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), Numero = r.StrOrNull("numero"), ClienteId = r.GuidOrNull("cliente_id"), ContactoId = r.GuidOrNull("contacto_id"),
        OportunidadId = r.GuidOrNull("oportunidad_id"), SedeId = r.GuidOrNull("sede_id"), FechaEmision = FechaOrNull(r, "fecha_emision")!.Value,
        ValidaHasta = FechaOrNull(r, "valida_hasta")!.Value, ModoIgv = Enum.Parse<ModoIgv>(r.Str("modo_igv")), Condiciones = r.StrOrNull("condiciones"),
        Estado = Enum.Parse<EstadoCotizacion>(r.Str("estado")), PedidoId = r.GuidOrNull("pedido_id"), CreadoEn = r.Time("creado_en"),
        Moneda = r.Str("moneda"), ResponsableId = r.GuidOrNull("responsable_id"), ResponsableNombre = r.StrOrNull("responsable_nombre"),
    };

    public static LineaCotizacion LeerLinea(SqliteDataReader r) => new()
    {
        ItemId = r.GuidOrNull("item_id"), Descripcion = r.Str("descripcion"), Unidad = r.Str("unidad"), Cantidad = r.Dec("cantidad"),
        PrecioUnitario = r.Dec("precio_unitario"),
    };

    public static Tarea LeerTarea(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), Titulo = r.Str("titulo"), Tipo = Enum.Parse<TipoTarea>(r.Str("tipo")), Fecha = FechaOrNull(r, "fecha")!.Value,
        Hora = r.StrOrNull("hora") is { } h ? TimeOnly.ParseExact(h, "HH:mm", CultureInfo.InvariantCulture) : null,
        AvisarMinutosAntes = r.IsDBNull(r.GetOrdinal("avisar_minutos_antes")) ? null : r.Int("avisar_minutos_antes"),
        ClienteId = r.GuidOrNull("cliente_id"), ContactoId = r.GuidOrNull("contacto_id"), OportunidadId = r.GuidOrNull("oportunidad_id"),
        ResponsableId = r.GuidOrNull("responsable_id"), ResponsableNombre = r.StrOrNull("responsable_nombre"), Notas = r.StrOrNull("notas"),
        CreadoEn = r.Time("creado_en"), CompletadaEn = r.TimeOrNull("completada_en"),
    };

    public static string? Fecha(DateOnly? fecha) => fecha?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? FechaOrNull(SqliteDataReader r, string columna) =>
        r.StrOrNull(columna) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
