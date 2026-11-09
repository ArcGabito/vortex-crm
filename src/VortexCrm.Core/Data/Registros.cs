using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Actividades;
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
        string? ResponsableNombre = null, Guid? EmbudoId = null, Guid? EtapaId = null, string? Competidor = null,
        IReadOnlyList<LineaOportunidadDatos>? Lineas = null);

    /// <summary>Producto de la oportunidad. Lineas null en <see cref="OportunidadDatos"/>: no cambian.</summary>
    public sealed record LineaOportunidadDatos(Guid? ItemId, string Descripcion, decimal Cantidad, decimal PrecioUnitario);

    public sealed record LeadDatos(
        Guid Id, string Nombre, string? Empresa, string? TipoDocumento, string? Documento, string? Telefono, string? Email, string Origen,
        string Estado, string? Notas, string? MotivoDescarte, Guid? ResponsableId, string? ResponsableNombre, Guid? ClienteId,
        Guid? ContactoId, Guid? OportunidadId, DateTimeOffset CreadoEn, DateTimeOffset? ConvertidoEn);

    public sealed record LineaDatos(Guid? ItemId, string Descripcion, string Unidad, decimal Cantidad, decimal PrecioUnitario,
        decimal? PrecioLista = null, bool Opcional = false);

    public sealed record CotizacionDatos(
        Guid Id, string? Numero, Guid? ClienteId, Guid? ContactoId, Guid? OportunidadId, Guid? SedeId, DateOnly FechaEmision,
        DateOnly ValidaHasta, string ModoIgv, string? Condiciones, string Estado, Guid? PedidoId, DateTimeOffset CreadoEn,
        IReadOnlyList<LineaDatos> Lineas, string Moneda = "PEN", Guid? ResponsableId = null, string? ResponsableNombre = null,
        int Version = 1, Guid? VersionDe = null, string? AprobadaPor = null, DateTimeOffset? AprobadaEn = null, decimal? DescuentoAprobado = null,
        Guid? EnlaceId = null, string? EnlaceUrl = null, string? FirmadaPor = null, string? FirmadaCorreo = null, DateTimeOffset? FirmadaEn = null,
        string? RespuestaCliente = null);

    /// <summary>Plantilla de cotización: se guarda entera (líneas incluidas) como JSON.</summary>
    public sealed record PlantillaCotizacionDatos(Guid Id, string Nombre, string? ModoIgv, string? Condiciones, int? DiasValidez,
        IReadOnlyList<LineaDatos> Lineas);

    public sealed record InteraccionDatos(
        Guid Id, Guid? ClienteId, Guid? ContactoId, Guid? OportunidadId, Guid? TareaId, string Tipo, DateTimeOffset Fecha, string Resumen,
        string? Resultado, Guid? ResponsableId, string? ResponsableNombre, DateTimeOffset CreadoEn);

    public sealed record PlantillaDatos(Guid Id, string Nombre, string Canal, string? Asunto, string Cuerpo);

    public sealed record TareaDatos(
        Guid Id, string Titulo, string Tipo, DateOnly Fecha, TimeOnly? Hora, int? AvisarMinutosAntes, Guid? ClienteId,
        Guid? ContactoId, Guid? OportunidadId, Guid? ResponsableId, string? ResponsableNombre, string? Notas,
        DateTimeOffset CreadoEn, DateTimeOffset? CompletadaEn, string? Origen = null);

    public sealed record DisparoDatos(Guid Id, Guid ReglaId, Guid RegistroId, DateTimeOffset CreadoEn);

    public sealed record AjustesDatos(string ModoIgv, string? Condiciones, IReadOnlyList<string>? MotivosPerdida = null,
        decimal? DescuentoMaximo = null);

    public sealed record EtapaDatos(Guid Id, string Nombre, string Equivale, int Probabilidad, string? Color);

    public sealed record EmbudoDatos(Guid Id, string Nombre, IReadOnlyList<EtapaDatos> Etapas, bool PorDefecto, bool Archivado);

    public sealed record MetaDatos(Guid Id, DateOnly Mes, Guid? ResponsableId, string? ResponsableNombre, decimal Monto);

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
                                                          notas, creado_en, cerrado_en, responsable_id, responsable_nombre, embudo_id, etapa_id,
                                                          competidor, actualizado_en)
                    VALUES ($id, $titulo, $cliente, $contacto, $monto, $etapa, $cierre, $motivo, $notas, $creado, $cerrado, $responsable,
                            $responsableNombre, $embudo, $etapaId, $competidor, $actualizado)
                    """, tx, ("$id", op.Id), ("$titulo", op.Titulo), ("$cliente", op.ClienteId), ("$contacto", op.ContactoId), ("$monto", op.Monto),
                    ("$etapa", op.Etapa), ("$cierre", Fecha(op.FechaCierreEstimada)), ("$motivo", op.MotivoPerdida), ("$notas", op.Notas),
                    ("$creado", op.CreadoEn), ("$cerrado", op.CerradoEn), ("$responsable", op.ResponsableId),
                    ("$responsableNombre", op.ResponsableNombre), ("$embudo", op.EmbudoId), ("$etapaId", op.EtapaId),
                    ("$competidor", op.Competidor), ("$actualizado", actualizado));
                if (op.Lineas is { } lineasOp)
                {
                    await c.ExecAsync("DELETE FROM oportunidad_lineas WHERE oportunidad_id = $id", tx, ("$id", op.Id));
                    var no = 0;
                    foreach (var l in lineasOp)
                        await c.ExecAsync("""
                            INSERT INTO oportunidad_lineas (oportunidad_id, linea, item_id, descripcion, cantidad, precio_unitario)
                            VALUES ($id, $n, $item, $descripcion, $cantidad, $precio)
                            """, tx, ("$id", op.Id), ("$n", ++no), ("$item", l.ItemId), ("$descripcion", l.Descripcion),
                            ("$cantidad", l.Cantidad), ("$precio", l.PrecioUnitario));
                }
                return true;

            case CrmRecordKinds.Cotizacion:
                var co = data.Deserialize<CotizacionDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO cotizaciones (id, numero, cliente_id, contacto_id, oportunidad_id, sede_id, fecha_emision, valida_hasta,
                                                         modo_igv, condiciones, estado, pedido_id, creado_en, moneda, responsable_id,
                                                         responsable_nombre, actualizado_en, version, version_de, aprobada_por, aprobada_en,
                                                         descuento_aprobado, enlace_id, enlace_url, firmada_por, firmada_correo,
                                                         firmada_en, respuesta_cliente)
                    VALUES ($id, $numero, $cliente, $contacto, $oportunidad, $sede, $emision, $valida, $modo, $condiciones, $estado, $pedido,
                            $creado, $moneda, $responsable, $responsableNombre, $actualizado, $version, $versionDe, $aprobadaPor, $aprobadaEn,
                            $descuentoAprobado, $enlace, $enlaceUrl, $firmadaPor, $firmadaCorreo, $firmadaEn, $respuesta)
                    """, tx, ("$id", co.Id), ("$numero", co.Numero), ("$cliente", co.ClienteId), ("$contacto", co.ContactoId),
                    ("$oportunidad", co.OportunidadId), ("$sede", co.SedeId), ("$emision", Fecha(co.FechaEmision)), ("$valida", Fecha(co.ValidaHasta)),
                    ("$modo", co.ModoIgv), ("$condiciones", co.Condiciones), ("$estado", co.Estado), ("$pedido", co.PedidoId),
                    ("$creado", co.CreadoEn), ("$moneda", co.Moneda ?? "PEN"), ("$responsable", co.ResponsableId),
                    ("$responsableNombre", co.ResponsableNombre), ("$actualizado", actualizado), ("$version", co.Version < 1 ? 1 : co.Version),
                    ("$versionDe", co.VersionDe), ("$aprobadaPor", co.AprobadaPor), ("$aprobadaEn", co.AprobadaEn),
                    ("$descuentoAprobado", co.DescuentoAprobado), ("$enlace", co.EnlaceId), ("$enlaceUrl", co.EnlaceUrl),
                    ("$firmadaPor", co.FirmadaPor), ("$firmadaCorreo", co.FirmadaCorreo), ("$firmadaEn", co.FirmadaEn),
                    ("$respuesta", co.RespuestaCliente));
                await c.ExecAsync("DELETE FROM cotizacion_lineas WHERE cotizacion_id = $id", tx, ("$id", co.Id));
                var n = 0;
                foreach (var l in co.Lineas ?? [])
                {
                    await c.ExecAsync("""
                        INSERT INTO cotizacion_lineas (cotizacion_id, linea, item_id, descripcion, unidad, cantidad, precio_unitario,
                                                       precio_lista, opcional)
                        VALUES ($id, $n, $item, $descripcion, $unidad, $cantidad, $precio, $lista, $opcional)
                        """, tx, ("$id", co.Id), ("$n", ++n), ("$item", l.ItemId), ("$descripcion", l.Descripcion), ("$unidad", l.Unidad),
                        ("$cantidad", l.Cantidad), ("$precio", l.PrecioUnitario), ("$lista", l.PrecioLista), ("$opcional", l.Opcional));
                }
                return true;

            case CrmRecordKinds.Tarea:
                var t = data.Deserialize<TareaDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO tareas (id, titulo, tipo, fecha, hora, avisar_minutos_antes, cliente_id, contacto_id, oportunidad_id,
                                                   responsable_id, responsable_nombre, notas, creado_en, completada_en, actualizado_en, origen)
                    VALUES ($id, $titulo, $tipo, $fecha, $hora, $avisar, $cliente, $contacto, $oportunidad, $responsable, $responsableNombre,
                            $notas, $creado, $completada, $actualizado, $origen)
                    """, tx, ("$id", t.Id), ("$titulo", t.Titulo), ("$tipo", t.Tipo), ("$fecha", Fecha(t.Fecha)), ("$hora", t.Hora?.ToString("HH:mm", CultureInfo.InvariantCulture)),
                    ("$avisar", t.AvisarMinutosAntes), ("$cliente", t.ClienteId), ("$contacto", t.ContactoId), ("$oportunidad", t.OportunidadId),
                    ("$responsable", t.ResponsableId), ("$responsableNombre", t.ResponsableNombre), ("$notas", t.Notas), ("$creado", t.CreadoEn),
                    ("$completada", t.CompletadaEn), ("$actualizado", actualizado), ("$origen", t.Origen));
                return true;

            case CrmRecordKinds.Ajustes:
                var a = data.Deserialize<AjustesDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO ajustes (id, modo_igv, condiciones, motivos_perdida, actualizado_en, descuento_maximo)
                    VALUES (1, $modo, $condiciones, $motivos, $actualizado, $descuento)
                    """, tx, ("$modo", a.ModoIgv), ("$condiciones", a.Condiciones), ("$descuento", a.DescuentoMaximo),
                    ("$motivos", a.MotivosPerdida is { Count: > 0 } m ? JsonSerializer.Serialize(m) : null), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Embudo:
                var em = data.Deserialize<EmbudoDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO embudos (id, nombre, etapas, por_defecto, archivado, actualizado_en)
                    VALUES ($id, $nombre, $etapas, $defecto, $archivado, $actualizado)
                    """, tx, ("$id", em.Id), ("$nombre", em.Nombre), ("$etapas", JsonSerializer.Serialize(em.Etapas ?? [], VortexJson.Options)),
                    ("$defecto", em.PorDefecto), ("$archivado", em.Archivado), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Meta:
                var me = data.Deserialize<MetaDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO metas (id, mes, responsable_id, responsable_nombre, monto, actualizado_en)
                    VALUES ($id, $mes, $responsable, $nombre, $monto, $actualizado)
                    """, tx, ("$id", me.Id), ("$mes", Fecha(me.Mes)), ("$responsable", me.ResponsableId), ("$nombre", me.ResponsableNombre),
                    ("$monto", me.Monto), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Lead:
                var le = data.Deserialize<LeadDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO leads (id, nombre, empresa, tipo_documento, documento, telefono, email, origen, estado, notas,
                                                  motivo_descarte, responsable_id, responsable_nombre, cliente_id, contacto_id, oportunidad_id,
                                                  creado_en, convertido_en, actualizado_en)
                    VALUES ($id, $nombre, $empresa, $tipo, $doc, $telefono, $email, $origen, $estado, $notas, $motivo, $responsable,
                            $responsableNombre, $cliente, $contacto, $oportunidad, $creado, $convertido, $actualizado)
                    """, tx, ("$id", le.Id), ("$nombre", le.Nombre), ("$empresa", le.Empresa), ("$tipo", le.TipoDocumento), ("$doc", le.Documento),
                    ("$telefono", le.Telefono), ("$email", le.Email), ("$origen", le.Origen), ("$estado", le.Estado), ("$notas", le.Notas),
                    ("$motivo", le.MotivoDescarte), ("$responsable", le.ResponsableId), ("$responsableNombre", le.ResponsableNombre),
                    ("$cliente", le.ClienteId), ("$contacto", le.ContactoId), ("$oportunidad", le.OportunidadId), ("$creado", le.CreadoEn),
                    ("$convertido", le.ConvertidoEn), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Interaccion:
                var it = data.Deserialize<InteraccionDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO interacciones (id, cliente_id, contacto_id, oportunidad_id, tarea_id, tipo, fecha, resumen, resultado,
                                                          responsable_id, responsable_nombre, creado_en, actualizado_en)
                    VALUES ($id, $cliente, $contacto, $oportunidad, $tarea, $tipo, $fecha, $resumen, $resultado, $responsable, $nombre,
                            $creado, $actualizado)
                    """, tx, ("$id", it.Id), ("$cliente", it.ClienteId), ("$contacto", it.ContactoId), ("$oportunidad", it.OportunidadId),
                    ("$tarea", it.TareaId), ("$tipo", it.Tipo), ("$fecha", it.Fecha), ("$resumen", it.Resumen), ("$resultado", it.Resultado),
                    ("$responsable", it.ResponsableId), ("$nombre", it.ResponsableNombre), ("$creado", it.CreadoEn), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.PlantillaCotizacion:
                var pc = data.Deserialize<PlantillaCotizacionDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO plantillas_cotizacion (id, nombre, datos, actualizado_en) VALUES ($id, $nombre, $datos, $actualizado)
                    """, tx, ("$id", pc.Id), ("$nombre", pc.Nombre), ("$datos", data.GetRawText()), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Regla or CrmRecordKinds.Secuencia or CrmRecordKinds.AsignacionLeads:
                await c.ExecAsync($"INSERT OR REPLACE INTO {Tabla(kind)} (id, datos, actualizado_en) VALUES ($id, $datos, $actualizado)", tx,
                    ("$id", data.GetProperty("id").GetGuid()), ("$datos", data.GetRawText()), ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Disparo:
                var dp = data.Deserialize<DisparoDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO disparos (id, regla_id, registro_id, creado_en, actualizado_en)
                    VALUES ($id, $regla, $registro, $creado, $actualizado)
                    """, tx, ("$id", dp.Id), ("$regla", dp.ReglaId), ("$registro", dp.RegistroId), ("$creado", dp.CreadoEn),
                    ("$actualizado", actualizado));
                return true;

            case CrmRecordKinds.Plantilla:
                var pl = data.Deserialize<PlantillaDatos>(VortexJson.Options)!;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO plantillas (id, nombre, canal, asunto, cuerpo, actualizado_en)
                    VALUES ($id, $nombre, $canal, $asunto, $cuerpo, $actualizado)
                    """, tx, ("$id", pl.Id), ("$nombre", pl.Nombre), ("$canal", pl.Canal), ("$asunto", pl.Asunto), ("$cuerpo", pl.Cuerpo),
                    ("$actualizado", actualizado));
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
        if (kind == CrmRecordKinds.Oportunidad && borrados > 0)
            await c.ExecAsync("DELETE FROM oportunidad_lineas WHERE oportunidad_id = $id", tx, ("$id", id));
        return borrados > 0;
    }

    private static string? Tabla(string kind) => kind switch
    {
        CrmRecordKinds.Contacto => "contactos",
        CrmRecordKinds.Oportunidad => "oportunidades",
        CrmRecordKinds.Cotizacion => "cotizaciones",
        CrmRecordKinds.Tarea => "tareas",
        CrmRecordKinds.Ajustes => "ajustes",
        CrmRecordKinds.Embudo => "embudos",
        CrmRecordKinds.Meta => "metas",
        CrmRecordKinds.Lead => "leads",
        CrmRecordKinds.Interaccion => "interacciones",
        CrmRecordKinds.Plantilla => "plantillas",
        CrmRecordKinds.PlantillaCotizacion => "plantillas_cotizacion",
        CrmRecordKinds.Regla => "reglas",
        CrmRecordKinds.Secuencia => "secuencias",
        CrmRecordKinds.AsignacionLeads => "configuracion",
        CrmRecordKinds.Disparo => "disparos",
        _ => null,
    };

    // ---------- Del dominio a los datos y de vuelta ----------

    public static ContactoDatos Datos(Contacto c) =>
        new(c.Id, c.ClienteId, c.Nombres.Trim(), c.Apellidos.Trim(), Limpio(c.Dni), Limpio(c.Telefono), Limpio(c.Email), Limpio(c.Cargo), c.CreadoEn);

    public static OportunidadDatos Datos(Oportunidad o) =>
        new(o.Id, o.Titulo.Trim(), o.ClienteId, o.ContactoId, o.Monto, o.Etapa.ToString(), o.FechaCierreEstimada, Limpio(o.MotivoPerdida),
            Limpio(o.Notas), o.CreadoEn, o.CerradoEn, o.ResponsableId, o.ResponsableNombre, o.EmbudoId, o.EtapaId, Limpio(o.Competidor),
            o.Lineas?.Select(l => new LineaOportunidadDatos(l.ItemId, l.Descripcion.Trim(), l.Cantidad, l.PrecioUnitario)).ToList());

    public static LeadDatos Datos(Lead l) =>
        new(l.Id, l.Nombre.Trim(), Limpio(l.Empresa), Limpio(l.TipoDocumento)?.ToUpperInvariant(), Limpio(l.Documento), Limpio(l.Telefono),
            Limpio(l.Email), l.Origen, l.Estado, Limpio(l.Notas), Limpio(l.MotivoDescarte), l.ResponsableId, l.ResponsableNombre, l.ClienteId,
            l.ContactoId, l.OportunidadId, l.CreadoEn, l.ConvertidoEn);

    public static Lead LeerLead(SqliteDataReader r) => Lead.Cargar(
        r.Guid("id"), r.Str("nombre"), r.StrOrNull("empresa"), r.StrOrNull("tipo_documento"), r.StrOrNull("documento"), r.StrOrNull("telefono"),
        r.StrOrNull("email"), r.Str("origen"), r.Str("estado"), r.StrOrNull("notas"), r.StrOrNull("motivo_descarte"), r.GuidOrNull("responsable_id"),
        r.StrOrNull("responsable_nombre"), r.GuidOrNull("cliente_id"), r.GuidOrNull("contacto_id"), r.GuidOrNull("oportunidad_id"),
        r.Time("creado_en"), r.TimeOrNull("convertido_en"));

    public static LineaOportunidad LeerLineaOportunidad(SqliteDataReader r) =>
        new(r.GuidOrNull("item_id"), r.Str("descripcion"), r.Dec("cantidad"), r.Dec("precio_unitario"));

    public static EmbudoDatos Datos(Embudo e) =>
        new(e.Id, e.Nombre.Trim(), e.Etapas.Select(x => new EtapaDatos(x.Id, x.Nombre.Trim(), x.Equivale.ToString(), x.Probabilidad, x.Color)).ToList(),
            e.PorDefecto, e.Archivado);

    public static MetaDatos Datos(Meta m) => new(m.Id, m.Mes, m.ResponsableId, m.ResponsableNombre, m.Monto);

    public static Embudo LeerEmbudo(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("nombre"),
        (JsonSerializer.Deserialize<List<EtapaDatos>>(r.Str("etapas"), VortexJson.Options) ?? [])
            .Select(x => new EtapaEmbudo(x.Id, x.Nombre, Enum.Parse<EtapaOportunidad>(x.Equivale), x.Probabilidad, x.Color)).ToList(),
        r.Bool("por_defecto"), r.Bool("archivado"));

    public static Meta LeerMeta(SqliteDataReader r) =>
        new(r.Guid("id"), FechaOrNull(r, "mes")!.Value, r.GuidOrNull("responsable_id"), r.StrOrNull("responsable_nombre"), r.Dec("monto"));

    public static CotizacionDatos Datos(Cotizacion c) =>
        new(c.Id, c.Numero, c.ClienteId, c.ContactoId, c.OportunidadId, c.SedeId, c.FechaEmision, c.ValidaHasta, c.ModoIgv.ToString(),
            Limpio(c.Condiciones), c.Estado.ToString(), c.PedidoId, c.CreadoEn,
            c.Lineas.Where(l => !l.EstaVacia).Select(DatosLinea).ToList(),
            c.Moneda, c.ResponsableId, c.ResponsableNombre, c.Version, c.VersionDe, c.AprobadaPor, c.AprobadaEn, c.DescuentoAprobado,
            c.EnlaceId, c.EnlaceUrl, c.FirmadaPor, c.FirmadaCorreo, c.FirmadaEn, c.RespuestaCliente);

    public static LineaDatos DatosLinea(LineaCotizacion l) =>
        new(l.ItemId, l.Descripcion.Trim(), l.Unidad, l.Cantidad, l.PrecioUnitario, l.PrecioLista, l.Opcional);

    public static LineaCotizacion LeerLinea(LineaDatos l) => new()
    {
        ItemId = l.ItemId, Descripcion = l.Descripcion, Unidad = l.Unidad, Cantidad = l.Cantidad, PrecioUnitario = l.PrecioUnitario,
        PrecioLista = l.PrecioLista, Opcional = l.Opcional,
    };

    public static InteraccionDatos Datos(Interaccion i) =>
        new(i.Id, i.ClienteId, i.ContactoId, i.OportunidadId, i.TareaId, i.Tipo.ToString(), i.Fecha, i.Resumen.Trim(), Limpio(i.Resultado),
            i.ResponsableId, i.ResponsableNombre, i.CreadoEn);

    public static Interaccion LeerInteraccion(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), ClienteId = r.GuidOrNull("cliente_id"), ContactoId = r.GuidOrNull("contacto_id"),
        OportunidadId = r.GuidOrNull("oportunidad_id"), TareaId = r.GuidOrNull("tarea_id"),
        Tipo = Enum.TryParse<TipoInteraccion>(r.Str("tipo"), out var tipo) ? tipo : TipoInteraccion.Nota, Fecha = r.Time("fecha"),
        Resumen = r.Str("resumen"), Resultado = r.StrOrNull("resultado"), ResponsableId = r.GuidOrNull("responsable_id"),
        ResponsableNombre = r.StrOrNull("responsable_nombre"), CreadoEn = r.Time("creado_en"),
    };

    public static PlantillaDatos Datos(Plantilla p) => new(p.Id, p.Nombre.Trim(), p.Canal.ToString(), Limpio(p.Asunto), p.Cuerpo.Trim());

    public static Plantilla LeerPlantilla(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), Nombre = r.Str("nombre"),
        Canal = Enum.TryParse<CanalPlantilla>(r.Str("canal"), out var canal) ? canal : CanalPlantilla.WhatsApp,
        Asunto = r.StrOrNull("asunto"), Cuerpo = r.Str("cuerpo"),
    };

    public static TareaDatos Datos(Tarea t) =>
        new(t.Id, t.Titulo.Trim(), t.Tipo.ToString(), t.Fecha, t.Hora, t.AvisarMinutosAntes, t.ClienteId, t.ContactoId, t.OportunidadId,
            t.ResponsableId, t.ResponsableNombre, Limpio(t.Notas), t.CreadoEn, t.CompletadaEn, t.Origen);

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
        EmbudoId = r.GuidOrNull("embudo_id"), EtapaId = r.GuidOrNull("etapa_id"), Competidor = r.StrOrNull("competidor"),
    };

    public static Cotizacion LeerCotizacion(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), Numero = r.StrOrNull("numero"), ClienteId = r.GuidOrNull("cliente_id"), ContactoId = r.GuidOrNull("contacto_id"),
        OportunidadId = r.GuidOrNull("oportunidad_id"), SedeId = r.GuidOrNull("sede_id"), FechaEmision = FechaOrNull(r, "fecha_emision")!.Value,
        ValidaHasta = FechaOrNull(r, "valida_hasta")!.Value, ModoIgv = Enum.Parse<ModoIgv>(r.Str("modo_igv")), Condiciones = r.StrOrNull("condiciones"),
        Estado = Enum.Parse<EstadoCotizacion>(r.Str("estado")), PedidoId = r.GuidOrNull("pedido_id"), CreadoEn = r.Time("creado_en"),
        Moneda = r.Str("moneda"), ResponsableId = r.GuidOrNull("responsable_id"), ResponsableNombre = r.StrOrNull("responsable_nombre"),
        Version = r.Int("version"), VersionDe = r.GuidOrNull("version_de"), AprobadaPor = r.StrOrNull("aprobada_por"),
        AprobadaEn = r.TimeOrNull("aprobada_en"), DescuentoAprobado = r.DecOrNull("descuento_aprobado"),
        EnlaceId = r.GuidOrNull("enlace_id"), EnlaceUrl = r.StrOrNull("enlace_url"), FirmadaPor = r.StrOrNull("firmada_por"),
        FirmadaCorreo = r.StrOrNull("firmada_correo"), FirmadaEn = r.TimeOrNull("firmada_en"), RespuestaCliente = r.StrOrNull("respuesta_cliente"),
    };

    public static LineaCotizacion LeerLinea(SqliteDataReader r) => new()
    {
        ItemId = r.GuidOrNull("item_id"), Descripcion = r.Str("descripcion"), Unidad = r.Str("unidad"), Cantidad = r.Dec("cantidad"),
        PrecioUnitario = r.Dec("precio_unitario"), PrecioLista = r.DecOrNull("precio_lista"), Opcional = r.Bool("opcional"),
    };

    public static Tarea LeerTarea(SqliteDataReader r) => new()
    {
        Id = r.Guid("id"), Titulo = r.Str("titulo"), Tipo = Enum.Parse<TipoTarea>(r.Str("tipo")), Fecha = FechaOrNull(r, "fecha")!.Value,
        Hora = r.StrOrNull("hora") is { } h ? TimeOnly.ParseExact(h, "HH:mm", CultureInfo.InvariantCulture) : null,
        AvisarMinutosAntes = r.IsDBNull(r.GetOrdinal("avisar_minutos_antes")) ? null : r.Int("avisar_minutos_antes"),
        ClienteId = r.GuidOrNull("cliente_id"), ContactoId = r.GuidOrNull("contacto_id"), OportunidadId = r.GuidOrNull("oportunidad_id"),
        ResponsableId = r.GuidOrNull("responsable_id"), ResponsableNombre = r.StrOrNull("responsable_nombre"), Notas = r.StrOrNull("notas"),
        CreadoEn = r.Time("creado_en"), CompletadaEn = r.TimeOrNull("completada_en"), Origen = r.StrOrNull("origen"),
    };

    public static string? Fecha(DateOnly? fecha) => fecha?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? FechaOrNull(SqliteDataReader r, string columna) =>
        r.StrOrNull(columna) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
