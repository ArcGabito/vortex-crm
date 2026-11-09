using Microsoft.Data.Sqlite;
using VortexCrm.Core.Data;
using VortexCrm.Core.Domain;
using VortexCrm.Core.Domain.Comun;
using VortexCrm.Core.Domain.Contactos;
using VortexCrm.Core.Domain.Ventas;
using VortexCrm.Core.Integration;
using VortexCrm.Core.Pdf;
using VortexCrm.Core.Sync;

using static VortexCrm.Core.Texts.CoreText;

namespace VortexCrm.Core.Services;

/// <summary>
/// Cotizaciones con IGV. Al aceptarse publican <c>quote.accepted</c> (y <c>deal.won</c> si ganan su oportunidad); desde
/// una aceptada se genera el pedido (<c>order.created</c>) con el mismo Id que calcularía el ERP, así nunca se duplica.
/// </summary>
public sealed class ServicioCotizaciones(CrmDatabase db, ICrmContext context, TimeProvider clock) : CrmService(db, context, clock)
{
    /// <summary>Id del pedido que sale de una cotización. El ERP usa el mismo cálculo (<c>OrderService.OrderIdForQuote</c>).</summary>
    public static Guid PedidoIdPara(Guid cotizacionId) => DeterministicId.From("order_from_quote", cotizacionId);

    public async Task<IReadOnlyList<CotizacionVista>> ListarAsync(string? busqueda = null, EstadoCotizacion? estado = null,
        Guid? clienteId = null, Guid? oportunidadId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (regimen, _) = await FiscalAsync(c, null);
        var tope = (await ServicioClientes.AjustesAsync(c)).DescuentoMaximo;
        return await c.QueryAsync("""
            SELECT q.*, p.name AS cliente,
                   (SELECT COALESCE(MAX((l.precio_lista - l.precio_unitario) * 100.0 / l.precio_lista), 0) FROM cotizacion_lineas l
                    WHERE l.cotizacion_id = q.id AND l.opcional = 0 AND l.precio_lista > 0) AS descuento,
                   (SELECT COALESCE(SUM(ROUND(l.cantidad * l.precio_unitario, 2)), 0) FROM cotizacion_lineas l WHERE l.cotizacion_id = q.id AND l.opcional = 0) AS suma
            FROM cotizaciones q LEFT JOIN parties p ON p.id = q.cliente_id
            WHERE ($estado IS NULL OR q.estado = $estado) AND ($cliente IS NULL OR q.cliente_id = $cliente)
              AND ($oportunidad IS NULL OR q.oportunidad_id = $oportunidad)
              AND ($q IS NULL OR q.numero LIKE $like OR p.name LIKE $like OR p.document_number LIKE $like)
            ORDER BY q.creado_en DESC
            """, r =>
            {
                var modo = Enum.Parse<ModoIgv>(r.Str("modo_igv"));
                var suma = r.Dec("suma");
                var decimales = VortexApi.Suite.Money.Currencies.Get(r.Str("moneda")).Decimals;
                var total = modo == ModoIgv.Adicional
                    ? suma + Math.Round(suma * regimen.StandardRate, decimales, MidpointRounding.AwayFromZero)
                    : suma;
                var numero = r.StrOrNull("numero") is { } n ? (r.Int("version") > 1 ? $"{n} v{r.Int("version")}" : n) : "Nueva";
                var estadoFila = Enum.Parse<EstadoCotizacion>(r.Str("estado"));
                var descuento = Math.Round(r.Dec("descuento"), 2, MidpointRounding.AwayFromZero);
                var porAprobar = tope is { } t && descuento > t && (r.DecOrNull("descuento_aprobado") ?? -1) < descuento
                    && estadoFila is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada;
                return new CotizacionVista(r.Guid("id"), numero, r.GuidOrNull("cliente_id"), r.StrOrNull("cliente"),
                    Enum.Parse<EstadoCotizacion>(r.Str("estado")), DateOnly.Parse(r.Str("fecha_emision"), System.Globalization.CultureInfo.InvariantCulture),
                    DateOnly.Parse(r.Str("valida_hasta"), System.Globalization.CultureInfo.InvariantCulture), total, r.GuidOrNull("oportunidad_id"),
                    r.GuidOrNull("pedido_id"), r.Time("creado_en"), r.Str("moneda"), porAprobar);
            }, null, ("$estado", estado?.ToString()), ("$cliente", clienteId), ("$oportunidad", oportunidadId), ("$q", Clean(busqueda)),
            ("$like", Like(busqueda)));
    }

    public async Task<Cotizacion?> ObtenerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ObtenerAsync(c, null, id);
    }

    /// <summary>Una cotización nueva con los ajustes de la organización (IGV, condiciones, validez) y la sede de este equipo.</summary>
    public async Task<Cotizacion> NuevaAsync(Guid? clienteId = null, Guid? oportunidadId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var ajustes = await ServicioClientes.AjustesAsync(c);
        var (regimen, moneda) = await FiscalAsync(c, null);
        var hoy = FormatoPeru.Hoy(Clock);
        var cotizacion = new Cotizacion
        {
            ClienteId = clienteId, OportunidadId = oportunidadId, SedeId = DefaultSite, ModoIgv = ajustes.ModoIgvPredeterminado,
            Condiciones = ajustes.CondicionesPredeterminadas, FechaEmision = hoy, ValidaHasta = hoy.AddDays(Cotizacion.DiasDeValidezPredeterminados),
            CreadoEn = Clock.GetUtcNow(), Moneda = moneda, Regimen = regimen, ResponsableId = Context.UserId,
            ResponsableNombre = Context.UserName,
        };
        if (oportunidadId is { } op && await ServicioOportunidades.ObtenerAsync(c, null, op) is { } oportunidad)
        {
            cotizacion.ClienteId ??= oportunidad.ClienteId;
            cotizacion.ContactoId = oportunidad.ContactoId;
        }
        cotizacion.Lineas.Add(new LineaCotizacion());
        return cotizacion;
    }

    /// <summary>Guarda la cotización (borrador o enviada). La primera vez recibe su número.</summary>
    public Task<string> GuardarAsync(Cotizacion cotizacion, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        cotizacion.Lineas.RemoveAll(l => l.EstaVacia);
        var errores = cotizacion.Validar();
        Require(errores.Count == 0, string.Join(" ", errores));
        Require(cotizacion.SedeId is not { } sede || Context.SiteIds.Count == 0 || Context.SiteIds.Contains(sede), T("error.sede_no_asignada"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var guardada = await ObtenerAsync(c, tx, cotizacion.Id);
            Require(guardada is null || guardada.Estado is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada,
                T("error.la_cotizacion_ya_fue_aceptada"));
            if (guardada is not null)
                RequireOwner(guardada.ResponsableId, guardada.ResponsableNombre);
            if (cotizacion.Estado == EstadoCotizacion.Enviada)
                await RequireDescuentoAprobadoAsync(c, tx, cotizacion);
            cotizacion.ResponsableId ??= Context.UserId;
            cotizacion.ResponsableNombre ??= Context.UserName;
            cotizacion.Numero ??= guardada?.Numero ?? await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Cotizacion, Context.DeviceTag);
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
            return cotizacion.Codigo;
        }, ct);
    }

    /// <summary>
    /// Cambia el estado y avanza la oportunidad vinculada (enviada → Cotizado, aceptada → Ganado). Al aceptarse publica
    /// <c>quote.accepted</c> con las líneas del catálogo, para que el ERP la convierta en pedido.
    /// </summary>
    public Task CambiarEstadoAsync(Guid id, EstadoCotizacion estado, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is not null, T("error.guarda_la_cotizacion_antes_de"));
            RequireOwner(cotizacion!.ResponsableId, cotizacion.ResponsableNombre);
            Require(cotizacion!.Estado != estado, T("error.la_cotizacion_ya_esta_en"));
            Require(cotizacion.PedidoId is null, T("error.la_cotizacion_ya_tiene_pedido"));
            Require(cotizacion.Estado != EstadoCotizacion.Reemplazada && estado != EstadoCotizacion.Reemplazada,
                T("error.cotizacion_reemplazada", cotizacion.Codigo));
            if (estado == EstadoCotizacion.Enviada)
                await RequireDescuentoAprobadoAsync(c, tx, cotizacion);
            await AplicarEstadoAsync(c, tx, now, cotizacion, estado);
            return 0;
        }, ct);
    }

    /// <summary>Cambia el estado, avanza la oportunidad, replica, audita y, al aceptarse, publica <c>quote.accepted</c>.</summary>
    private async Task AplicarEstadoAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Cotizacion cotizacion,
        EstadoCotizacion estado, string? detalle = null)
    {
        var oportunidad = cotizacion.OportunidadId is { } op ? await ServicioOportunidades.ObtenerAsync(c, tx, op) : null;
        var etapaAnterior = oportunidad?.Etapa;
        var cambioOportunidad = cotizacion.CambiarEstado(estado, oportunidad, now);
        await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
        if (cambioOportunidad)
            await ServicioOportunidades.GuardarAsync(c, tx, now, oportunidad!, etapaAnterior);
        await AuditAsync(c, tx, now, "quote." + estado.ToString().ToLowerInvariant(), "quote", cotizacion.Id, cotizacion.Numero,
            detalle ?? $"Total {cotizacion.Totales.Total:0.##} {cotizacion.Moneda}");

        if (estado == EstadoCotizacion.Aceptada && cotizacion.ClienteId is { } cliente)
        {
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.QuoteAccepted,
                new QuoteAccepted(cotizacion.Id, cotizacion.Numero, cliente, LineasDelCatalogo(cotizacion), cotizacion.Totales.Total, cotizacion.Moneda),
                subjectType: "quote", subjectId: cotizacion.Numero, id: DeterministicId.From("quote_accepted", cotizacion.Id));
        }
    }

    /// <summary>Genera el pedido de una cotización aceptada: el WMS lo prepara y el ERP lo factura.</summary>
    public Task<string> GenerarPedidoAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is not null, T("error.la_cotizacion_no_existe"));
            RequireOwner(cotizacion!.ResponsableId, cotizacion.ResponsableNombre);
            Require(cotizacion!.Estado == EstadoCotizacion.Aceptada, T("error.solo_se_genera_el_pedido"));
            Require(cotizacion.PedidoId is null, T("error.la_cotizacion_ya_tiene_pedido_2"));
            Require(PuedeGenerarPedido(cotizacion), T("error.para_generar_el_pedido_todas"));
            return await PedidoAsync(c, tx, now, cotizacion);
        }, ct);
    }

    /// <summary>Todas las líneas que se compran son del catálogo (el ERP y el WMS necesitan el artículo).</summary>
    private static bool PuedeGenerarPedido(Cotizacion cotizacion) =>
        cotizacion.ClienteId is not null && cotizacion.Lineas.Where(l => !l.Opcional).All(l => l.ItemId is not null);

    /// <param name="publicar">false si quien llama publica la cotización después en la misma transacción (los demás equipos
    /// solo aplican un registro más nuevo: dos publicaciones con la misma hora dejarían la segunda sin replicar).</param>
    private async Task<string> PedidoAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Cotizacion cotizacion,
        bool publicar = true)
    {
        var pedidoId = PedidoIdPara(cotizacion.Id);
        var numero = "PED-" + cotizacion.Numero!.Replace("COT-", "", StringComparison.Ordinal)
            + (cotizacion.Version > 1 ? $"-V{cotizacion.Version}" : "");
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderCreated,
            new OrderCreated(pedidoId, numero, cotizacion.ClienteId!.Value, cotizacion.SedeId, LineasDelCatalogo(cotizacion),
                cotizacion.Totales.Total, cotizacion.Moneda, cotizacion.Id),
            subjectType: "order", subjectId: numero, id: pedidoId);
        cotizacion.PedidoId = pedidoId;
        if (publicar)
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
        await AuditAsync(c, tx, now, "quote.order_created", "quote", cotizacion.Id, cotizacion.Numero, numero);
        return numero;
    }

    public Task EliminarAsync(Guid id, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is null || cotizacion.Estado == EstadoCotizacion.Borrador, T("error.solo_se_elimina_una_cotizacion"));
            if (cotizacion is not null)
                RequireOwner(cotizacion.ResponsableId, cotizacion.ResponsableNombre);
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.Cotizacion, id);
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Deleted, "quote", id, cotizacion?.Numero, null);
            return 0;
        }, ct);

    // ---------- Precios de lista (CRM-33) ----------

    /// <summary>
    /// La lista que rige para el cliente (si no tiene, la de la sede) en la moneda de la cotización y vigente hoy. Una lista en
    /// otra moneda no rige (el precio es el del artículo).
    /// </summary>
    public async Task<ListaPrecios> ListaPreciosAsync(Guid? clienteId, Guid? sedeId, string moneda, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ListaPreciosAsync(c, null, clienteId, sedeId ?? DefaultSite, moneda);
    }

    internal async Task<ListaPrecios> ListaPreciosAsync(SqliteConnection c, SqliteTransaction? tx, Guid? clienteId, Guid? sedeId, string moneda)
    {
        var hoy = FormatoPeru.Hoy(Clock).ToString("yyyy-MM-dd");
        const string vigente = """
            l.is_active = 1 AND l.is_deleted = 0 AND l.currency_code = $moneda
            AND (l.valid_from IS NULL OR l.valid_from <= $hoy) AND (l.valid_to IS NULL OR l.valid_to >= $hoy)
            """;
        var propia = clienteId is { } id ? await c.ScalarAsync<string?>("SELECT price_list_id FROM parties WHERE id = $id", tx, ("$id", id)) : null;
        (Guid Id, string Nombre) lista = default;
        if (propia is not null)
            lista = (await c.QueryAsync($"SELECT l.id, l.name FROM price_lists l WHERE l.id = $id AND {vigente}",
                r => (r.Guid("id"), r.Str("name")), tx, ("$id", propia), ("$moneda", moneda), ("$hoy", hoy))).FirstOrDefault();
        if (lista.Id == Guid.Empty && sedeId is { } sede)
            lista = (await c.QueryAsync($"""
                SELECT l.id, l.name FROM price_lists l
                WHERE {vigente} AND EXISTS (SELECT 1 FROM json_each(l.site_ids) WHERE value = $sede) ORDER BY l.code LIMIT 1
                """, r => (r.Guid("id"), r.Str("name")), tx, ("$sede", sede.ToString()), ("$moneda", moneda), ("$hoy", hoy))).FirstOrDefault();
        if (lista.Id == Guid.Empty)
            return ListaPrecios.Ninguna;
        var precios = (await c.QueryAsync("SELECT item_id, min_qty, price FROM price_list_lines WHERE list_id = $id",
                r => (Item: r.Guid("item_id"), Min: r.Dec("min_qty"), Precio: r.Dec("price")), tx, ("$id", lista.Id)))
            .GroupBy(x => x.Item)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(decimal, decimal)>)g.Select(x => (x.Min, x.Precio)).OrderBy(x => x.Min).ToList());
        return new ListaPrecios(lista.Id, lista.Nombre, precios);
    }

    /// <summary>
    /// Vuelve a poner el precio de lista a las líneas del catálogo (cambió el cliente, la cantidad, la moneda o el impuesto).
    /// Una línea con el precio de lista toma el nuevo; una con precio a mano lo conserva y su descuento se mide contra el nuevo.
    /// Devuelve la lista que rige.
    /// </summary>
    public async Task<ListaPrecios> RepreciarAsync(Cotizacion cotizacion, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        cotizacion.Regimen = (await FiscalAsync(c, null)).Regimen;
        var lista = await ListaPreciosAsync(c, null, cotizacion.ClienteId, cotizacion.SedeId ?? DefaultSite, cotizacion.Moneda);
        foreach (var linea in cotizacion.Lineas.Where(l => l.ItemId is not null))
        {
            var item = await c.FirstOrDefaultAsync("SELECT i.*, NULL AS on_hand FROM items i WHERE i.id = $id", MasterDataService.ReadItem, null,
                ("$id", linea.ItemId));
            if (item is null)
                continue;
            var nuevo = item.PrecioPara(cotizacion, lista.PrecioDe(item.Id, linea.Cantidad));
            if (linea.PrecioLista is null || linea.PrecioUnitario == linea.PrecioLista)
                linea.PrecioUnitario = nuevo;
            linea.PrecioLista = nuevo;
        }
        return lista;
    }

    // ---------- Descuentos (CRM-34) ----------

    /// <summary>El gerente aprueba el descuento de la cotización tal como está (si después baja más, hay que volver a aprobar).</summary>
    public Task AprobarDescuentoAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.ApproveDiscounts);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is not null, T("error.la_cotizacion_no_existe"));
            Require(cotizacion!.Estado is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada, T("error.la_cotizacion_ya_fue_aceptada"));
            cotizacion.AprobadaPor = Context.UserName;
            cotizacion.AprobadaEn = now;
            cotizacion.DescuentoAprobado = cotizacion.DescuentoMaximo;
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
            await AuditAsync(c, tx, now, "quote.discount_approved", "quote", id, cotizacion.Numero,
                $"{cotizacion.DescuentoMaximo.ToString("0.##", FormatoPeru.Cultura)} %");
            return 0;
        }, ct);
    }

    /// <summary>Un vendedor no envía una cotización con más descuento que su tope sin la aprobación del gerente.</summary>
    private async Task RequireDescuentoAprobadoAsync(SqliteConnection c, SqliteTransaction? tx, Cotizacion cotizacion)
    {
        if (Context.Can(CrmPermissions.ApproveDiscounts))
            return;
        var tope = (await ServicioClientes.AjustesAsync(c, tx)).DescuentoMaximo;
        Require(!cotizacion.RequiereAprobacion(tope), T("error.descuento_requiere_aprobacion",
            cotizacion.DescuentoMaximo.ToString("0.##", FormatoPeru.Cultura), tope?.ToString("0.##", FormatoPeru.Cultura)));
    }

    // ---------- Versiones (CRM-35) ----------

    /// <summary>
    /// Nueva versión de una cotización enviada o rechazada: copia las líneas y condiciones con el mismo número (v2, v3…), queda
    /// en borrador y la anterior pasa a Reemplazada. Devuelve el Id de la nueva.
    /// </summary>
    public async Task<Guid> NuevaVersionAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        Guid? enlaceAnterior = null;
        var nuevaId = await InTransactionAsync(async (c, tx, now) =>
        {
            var anterior = await ObtenerAsync(c, tx, id);
            Require(anterior is not null, T("error.la_cotizacion_no_existe"));
            RequireOwner(anterior!.ResponsableId, anterior.ResponsableNombre);
            Require(anterior.Estado is EstadoCotizacion.Enviada or EstadoCotizacion.Rechazada && anterior.PedidoId is null,
                T("error.solo_version_de_enviada"));
            var hoy = FormatoPeru.Hoy(Clock);
            var nueva = anterior.Clonar();
            nueva.Id = Guid.NewGuid();
            nueva.Version = await c.ScalarAsync<int>("SELECT COALESCE(MAX(version), 1) FROM cotizaciones WHERE id = $raiz OR version_de = $raiz", tx,
                ("$raiz", anterior.VersionDe ?? anterior.Id)) + 1;
            nueva.VersionDe = anterior.VersionDe ?? anterior.Id;
            nueva.Estado = EstadoCotizacion.Borrador;
            nueva.CreadoEn = now;
            nueva.ValidaHasta = hoy.AddDays(Math.Max(anterior.ValidaHasta.DayNumber - anterior.FechaEmision.DayNumber, 1));
            nueva.FechaEmision = hoy;
            (nueva.AprobadaPor, nueva.AprobadaEn, nueva.DescuentoAprobado) = (null, null, null);
            (nueva.EnlaceId, nueva.EnlaceUrl, nueva.FirmadaPor, nueva.FirmadaCorreo, nueva.FirmadaEn, nueva.RespuestaCliente) =
                (null, null, null, null, null, null);
            anterior.Estado = EstadoCotizacion.Reemplazada;
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, anterior.Id, Registros.Datos(anterior));
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, nueva.Id, Registros.Datos(nueva));
            await AuditAsync(c, tx, now, "quote.new_version", "quote", nueva.Id, nueva.Numero, nueva.Codigo);
            enlaceAnterior = anterior.EnlaceId;
            return nueva.Id;
        }, ct);
        // El enlace de la versión anterior deja de aceptar respuestas (sin conexión queda abierto: si el cliente la acepta,
        // la respuesta llega a una versión reemplazada y queda como interacción para el vendedor).
        if (enlaceAnterior is { } enlace)
            await RetirarEnlaceAsync(enlace, ct);
        return nuevaId;
    }

    /// <summary>Todas las versiones de la cotización (de la primera a la última).</summary>
    public async Task<IReadOnlyList<(Guid Id, int Version, EstadoCotizacion Estado, DateOnly Fecha)>> VersionesAsync(Cotizacion cotizacion,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT id, version, estado, fecha_emision FROM cotizaciones WHERE id = $raiz OR version_de = $raiz ORDER BY version",
            r => (r.Guid("id"), r.Int("version"), Enum.Parse<EstadoCotizacion>(r.Str("estado")),
                DateOnly.Parse(r.Str("fecha_emision"), System.Globalization.CultureInfo.InvariantCulture)),
            null, ("$raiz", cotizacion.VersionDe ?? cotizacion.Id));
    }

    // ---------- Plantillas de cotización (CRM-32) ----------

    public async Task<IReadOnlyList<PlantillaCotizacionVista>> PlantillasAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (await PlantillasAsync(c, null))
            .Select(p => new PlantillaCotizacionVista(p.Id, p.Nombre, p.Lineas.Select(l => l.Descripcion).ToList())).ToList();
    }

    private static Task<List<Registros.PlantillaCotizacionDatos>> PlantillasAsync(SqliteConnection c, Guid? id) =>
        c.QueryAsync("SELECT datos FROM plantillas_cotizacion WHERE $id IS NULL OR id = $id ORDER BY nombre",
            r => System.Text.Json.JsonSerializer.Deserialize<Registros.PlantillaCotizacionDatos>(r.Str("datos"), VortexApi.Client.VortexJson.Options)!,
            null, ("$id", id));

    /// <summary>Guarda las líneas, condiciones, impuesto y validez de la cotización como plantilla (la comparte todo el equipo).</summary>
    public Task<Guid> GuardarPlantillaAsync(string nombre, Cotizacion desde, Guid? reemplaza = null, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        Require(!string.IsNullOrWhiteSpace(nombre), T("error.la_plantilla_necesita_nombre"));
        var lineas = desde.Lineas.Where(l => !l.EstaVacia).Select(Registros.DatosLinea).ToList();
        Require(lineas.Count > 0, T("error.agrega_al_menos_un_producto"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var id = reemplaza ?? Guid.NewGuid();
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.PlantillaCotizacion, id,
                new Registros.PlantillaCotizacionDatos(id, nombre.Trim(), desde.ModoIgv.ToString(), Clean(desde.Condiciones),
                    Math.Max(desde.ValidaHasta.DayNumber - desde.FechaEmision.DayNumber, 1), lineas));
            return id;
        }, ct);
    }

    public Task EliminarPlantillaAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.EditSettings);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await Registros.EliminarAsync(c, tx, now, CrmRecordKinds.PlantillaCotizacion, id);
            return 0;
        }, ct);
    }

    /// <summary>
    /// Llena la cotización con la plantilla: sus líneas (los artículos con el precio de lista de hoy para este cliente), sus
    /// condiciones, el modo de impuesto y la validez.
    /// </summary>
    public async Task AplicarPlantillaAsync(Cotizacion cotizacion, Guid plantillaId, CancellationToken ct = default)
    {
        Registros.PlantillaCotizacionDatos? plantilla;
        await using (var c = await Db.OpenAsync(ct))
            plantilla = (await PlantillasAsync(c, plantillaId)).FirstOrDefault();
        Require(plantilla is not null, T("error.la_plantilla_no_existe"));
        if (Enum.TryParse<ModoIgv>(plantilla!.ModoIgv, out var modo))
            cotizacion.ModoIgv = modo;
        if (plantilla.Condiciones is not null)
            cotizacion.Condiciones = plantilla.Condiciones;
        if (plantilla.DiasValidez is > 0 and var dias)
            cotizacion.ValidaHasta = cotizacion.FechaEmision.AddDays(dias);
        cotizacion.Lineas.RemoveAll(l => l.EstaVacia);
        foreach (var l in plantilla.Lineas)
        {
            var linea = Registros.LeerLinea(l);
            linea.PrecioLista = null; // se vuelve a poner con la lista de hoy
            cotizacion.Lineas.Add(linea);
        }
        await RepreciarAsync(cotizacion, ct);
    }

    // ---------- Aceptación en línea (CRM-36) ----------

    /// <summary>
    /// Enlace público del hub para que el cliente vea la cotización y la acepte con su firma (o la rechace). Necesita conexión.
    /// Lleva una copia de la cotización tal como está; pedirlo otra vez (tras editarla) actualiza la copia con la misma URL.
    /// Una cotización en borrador pasa a Enviada.
    /// </summary>
    public async Task<string> EnlaceParaAceptarAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(CrmPermissions.Sell);
        var hub = Context.Enlaces;
        Require(hub is not null, T("error.enlace_sin_conexion"));
        VortexApi.Client.VortexPublicLinkRequest pedido;
        await using (var c = await Db.OpenAsync(ct))
        {
            var (cotizacion, cliente, contacto, empresa) = await DatosDeEnvioAsync(c, id);
            RequireOwner(cotizacion.ResponsableId, cotizacion.ResponsableNombre);
            Require(cotizacion.Estado is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada, T("error.enlace_solo_abierta"));
            Require(cotizacion.ValidaHasta >= FormatoPeru.Hoy(Clock), T("error.enlace_vencida"));
            var emisor = await EmisorAsync(c, cotizacion, empresa);
            pedido = new("quote", cotizacion.Id, VenceEn(cotizacion), DocumentoPublico(cotizacion, emisor, ClienteCotizacion.Crear(cliente, contacto)),
                Texts.CoreText.Language);
        }

        VortexApi.Client.VortexPublicLink enlace;
        try
        {
            enlace = await hub!.CrearAsync(pedido, ct);
        }
        catch (Exception ex) when (ex is VortexApi.Client.VortexApiException or HttpRequestException or TaskCanceledException)
        {
            throw new CrmValidationException(T("error.enlace_sin_conexion"));
        }

        await InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = (await ObtenerAsync(c, tx, id))!;
            var nuevo = cotizacion.EnlaceId != enlace.Id || cotizacion.EnlaceUrl != enlace.Url;
            (cotizacion.EnlaceId, cotizacion.EnlaceUrl) = (enlace.Id, enlace.Url);
            if (cotizacion.Estado == EstadoCotizacion.Borrador)
                await AplicarEstadoAsync(c, tx, now, cotizacion, EstadoCotizacion.Enviada, T("cotizacion.enlace_compartido"));
            else if (nuevo)
                await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Cotizacion, cotizacion.Id, Registros.Datos(cotizacion));
            await AuditAsync(c, tx, now, "quote.link_shared", "quote", id, cotizacion.Numero, enlace.Url);
            return 0;
        }, ct);
        return enlace.Url;
    }

    /// <summary>Cómo va el enlace en el hub (vistas, respuesta y firma); null sin conexión o sin enlace.</summary>
    public async Task<VortexApi.Client.VortexPublicLink?> EstadoEnlaceAsync(Cotizacion cotizacion, CancellationToken ct = default)
    {
        if (cotizacion.EnlaceId is not { } id || Context.Enlaces is not { } hub)
            return null;
        try
        {
            return await hub.ObtenerAsync(id, ct);
        }
        catch (Exception ex) when (ex is VortexApi.Client.VortexApiException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task RetirarEnlaceAsync(Guid enlace, CancellationToken ct)
    {
        if (Context.Enlaces is not { } hub)
            return;
        try
        {
            await hub.RetirarAsync(enlace, ct);
        }
        catch (Exception ex) when (ex is VortexApi.Client.VortexApiException or HttpRequestException or TaskCanceledException)
        {
            // Sin conexión, o el cliente ya respondió: la respuesta, si llega, se trata como de una versión reemplazada.
        }
    }

    /// <summary>
    /// Respuesta del cliente desde el enlace (<c>hub.public_link.accepted|rejected</c>). Aceptada: queda Aceptada con el
    /// firmante, gana su oportunidad, publica <c>quote.accepted</c> y genera el pedido si todas sus líneas son del catálogo.
    /// Rechazada: queda Rechazada con el comentario. En los dos casos queda una interacción en la ficha del cliente. La
    /// reciben todos los equipos: los Ids deterministas evitan duplicados y lo que ya se aplicó no se repite.
    /// </summary>
    internal async Task<string?> AplicarRespuestaEnLineaAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now,
        VortexApi.Client.VortexPublicLinkResponded r)
    {
        if (r.Product != CrmSyncHandlers.ProductCode || r.DocumentType != "quote")
            return "ignorado: enlace de otro documento";
        var cotizacion = await ObtenerAsync(c, tx, r.DocumentId);
        if (cotizacion is null)
            return "la cotización no está en este equipo";
        if (cotizacion.FirmadaEn is not null || cotizacion.Estado is EstadoCotizacion.Aceptada)
            return "ya aplicada";

        var abierta = cotizacion.Estado is EstadoCotizacion.Borrador or EstadoCotizacion.Enviada;
        var firmante = r.SignerName ?? T("cotizacion.el_cliente");
        var resumen = !abierta
            ? T(r.Accepted ? "cotizacion.aceptada_no_vigente" : "cotizacion.rechazada_no_vigente", cotizacion.Codigo, firmante,
                Fmt(cotizacion.Estado))
            : T(r.Accepted ? "cotizacion.aceptada_en_linea" : "cotizacion.rechazada_en_linea", cotizacion.Codigo, firmante);
        var interaccion = new Domain.Actividades.Interaccion
        {
            Id = DeterministicId.From("quote_online_response", r.LinkId),
            ClienteId = cotizacion.ClienteId, ContactoId = cotizacion.ContactoId, OportunidadId = cotizacion.OportunidadId,
            Tipo = Domain.Actividades.TipoInteraccion.Nota, Fecha = r.RespondedAt, CreadoEn = now, Resumen = resumen,
            Resultado = r.Comment, ResponsableId = cotizacion.ResponsableId, ResponsableNombre = cotizacion.ResponsableNombre,
        };
        await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Interaccion, interaccion.Id, Registros.Datos(interaccion));
        if (!abierta)
            return $"respuesta a una cotización {cotizacion.Estado}";

        (cotizacion.FirmadaPor, cotizacion.FirmadaCorreo, cotizacion.FirmadaEn, cotizacion.RespuestaCliente) =
            (r.SignerName, r.SignerEmail, r.RespondedAt, r.Comment);
        var detalle = T(r.Accepted ? "cotizacion.aceptada_en_linea" : "cotizacion.rechazada_en_linea", cotizacion.Codigo, firmante);
        // Primero el estado (quote.accepted antes que order.created) y la cotización se publica una sola vez, ya con su pedido.
        var pedido = r.Accepted && PuedeGenerarPedido(cotizacion);
        if (pedido)
            cotizacion.PedidoId = PedidoIdPara(cotizacion.Id);
        await AplicarEstadoAsync(c, tx, now, cotizacion, r.Accepted ? EstadoCotizacion.Aceptada : EstadoCotizacion.Rechazada, detalle);
        if (pedido)
            await PedidoAsync(c, tx, now, cotizacion, publicar: false);
        return null;
    }

    private static string Fmt(EstadoCotizacion estado) => T("enum.cotizacion." + estado.ToString().ToLowerInvariant());

    /// <summary>Vence al terminar el día de validez (hora de la empresa), como máximo en un año.</summary>
    private DateTimeOffset VenceEn(Cotizacion cotizacion)
    {
        var fin = new DateTimeOffset(cotizacion.ValidaHasta.AddDays(1).ToDateTime(TimeOnly.MinValue), FormatoPeru.ZonaHoraria);
        var maximo = Clock.GetUtcNow().AddDays(365);
        return fin < maximo ? fin : maximo;
    }

    /// <summary>La cotización como la ve el cliente en el enlace: lo mismo que el PDF (opcionales aparte, totales con impuesto).</summary>
    internal static VortexApi.Client.VortexPublicDocument DocumentoPublico(Cotizacion q, EmisorPdf emisor, ClienteCotizacion cliente)
    {
        var totales = q.Totales;
        var filas = new List<VortexApi.Client.VortexPublicDocumentTotal>();
        if (q.ModoIgv != ModoIgv.NoAplica)
        {
            filas.Add(new(T("pdf.subtotal"), totales.Subtotal));
            filas.Add(new($"{q.NombreImpuesto} ({(q.TasaImpuesto * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} %)", totales.Igv));
        }
        filas.Add(new(T("pdf.total"), totales.Total, Grand: true));
        return new(
            T("cotizacion.titulo_enlace"), q.Codigo, q.FechaEmision, q.ValidaHasta, q.Moneda,
            new(emisor.Nombre, emisor.Ruc, emisor.Direccion, emisor.Email, emisor.Telefono),
            new(cliente.Nombre, cliente.Documento, cliente.Direccion, cliente.Email, cliente.Telefono),
            q.Lineas.Where(l => !l.EstaVacia)
                .Select(l => new VortexApi.Client.VortexPublicDocumentLine(l.Descripcion, l.Cantidad, l.Unidad, l.PrecioUnitario, l.Importe, l.Opcional))
                .ToList(),
            filas, q.Condiciones, VortexApi.Suite.Money.Currencies.Get(q.Moneda).Decimals, q.ResponsableNombre);
    }

    // ---------- Envío ----------

    /// <summary>Texto y enlace de WhatsApp (null si el teléfono no es un celular).</summary>
    public async Task<(string Mensaje, string? Enlace)> WhatsAppAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (cotizacion, cliente, contacto, empresa) = await DatosDeEnvioAsync(c, id);
        var saludo = contacto?.Nombres ?? (cliente.Kind == "Person" ? cliente.Name.Split(' ')[0] : null);
        var mensaje = MensajeWhatsApp.Cotizacion(cotizacion, empresa?.DisplayName ?? "nuestra empresa", saludo);
        return (mensaje, MensajeWhatsApp.Enlace(contacto?.Telefono ?? cliente.Phone, mensaje));
    }

    /// <summary>
    /// Datos para enviar la cotización por correo: a quién (el correo del contacto o, si no tiene, el del cliente), el asunto
    /// y el texto (el mismo saludo que el mensaje de WhatsApp). El PDF se adjunta aparte.
    /// </summary>
    public async Task<(string? Para, string Asunto, string Cuerpo)> CorreoAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (cotizacion, cliente, contacto, empresa) = await DatosDeEnvioAsync(c, id);
        var saludo = contacto?.Nombres ?? (cliente.Kind == "Person" ? cliente.Name.Split(' ')[0] : null);
        var emisor = empresa?.DisplayName ?? T("pdf.mi_empresa");
        var cuerpo = MensajeWhatsApp.Cotizacion(cotizacion, emisor, saludo).Replace("*", "");
        var para = !string.IsNullOrWhiteSpace(contacto?.Email) ? contacto!.Email : cliente.Email;
        return (para, T("cotizacion.asunto_correo", cotizacion.Codigo, emisor), cuerpo);
    }

    /// <summary>Deja constancia en la ficha de que la cotización se envió por correo.</summary>
    public Task RegistrarEnvioPorCorreoAsync(Guid id, string? para, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var cotizacion = await ObtenerAsync(c, tx, id);
            Require(cotizacion is not null, T("error.guarda_la_cotizacion_primero"));
            var interaccion = new Domain.Actividades.Interaccion
            {
                ClienteId = cotizacion!.ClienteId, ContactoId = cotizacion.ContactoId, OportunidadId = cotizacion.OportunidadId,
                Tipo = Domain.Actividades.TipoInteraccion.Correo, Fecha = now, CreadoEn = now,
                Resumen = T("cotizacion.enviada_por_correo", cotizacion.Codigo), Resultado = para,
                ResponsableId = Context.UserId, ResponsableNombre = Context.UserName,
            };
            await Registros.PublicarAsync(c, tx, now, CrmRecordKinds.Interaccion, interaccion.Id, Registros.Datos(interaccion));
            return 0;
        }, ct);

    public async Task<byte[]> GenerarPdfAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (cotizacion, cliente, contacto, empresa) = await DatosDeEnvioAsync(c, id);
        return GeneradorPdfCotizaciones.Generar(cotizacion, await EmisorAsync(c, cotizacion, empresa), ClienteCotizacion.Crear(cliente, contacto));
    }

    /// <summary>Quién emite: la empresa, con la dirección y el teléfono de la sede si la cotización sale de una.</summary>
    private static async Task<EmisorPdf> EmisorAsync(SqliteConnection c, Cotizacion cotizacion, CompanyInfo? empresa)
    {
        var sede = cotizacion.SedeId is { } s
            ? await c.FirstOrDefaultAsync("SELECT address_line, city, phone FROM sites WHERE id = $id",
                r => new { Linea = r.StrOrNull("address_line"), Ciudad = r.StrOrNull("city"), Telefono = r.StrOrNull("phone") }, null, ("$id", s))
            : null;
        var direccion = string.Join(", ", new[] { sede?.Linea ?? empresa?.AddressLine, sede?.Ciudad ?? empresa?.City }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        return new EmisorPdf(empresa?.DisplayName ?? T("pdf.mi_empresa"), empresa?.LegalName, empresa?.TaxId,
            direccion.Length == 0 ? null : direccion, sede?.Telefono ?? empresa?.Phone, empresa?.Email);
    }

    // ---------- Internos ----------

    internal static async Task<Cotizacion?> ObtenerAsync(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        var cotizacion = await c.FirstOrDefaultAsync("SELECT * FROM cotizaciones WHERE id = $id", Registros.LeerCotizacion, tx, ("$id", id));
        if (cotizacion is null)
            return null;
        cotizacion.Lineas = await c.QueryAsync("SELECT * FROM cotizacion_lineas WHERE cotizacion_id = $id ORDER BY linea",
            Registros.LeerLinea, tx, ("$id", id));
        cotizacion.Regimen = (await FiscalAsync(c, tx)).Regimen;
        return cotizacion;
    }

    private async Task<(Cotizacion, PartyRow, Contacto?, CompanyInfo?)> DatosDeEnvioAsync(SqliteConnection c, Guid id)
    {
        var cotizacion = await ObtenerAsync(c, null, id);
        Require(cotizacion is not null, T("error.guarda_la_cotizacion_primero"));
        var cliente = cotizacion!.ClienteId is { } cl
            ? await c.FirstOrDefaultAsync("SELECT * FROM parties WHERE id = $id", MasterDataService.ReadParty, null, ("$id", cl))
            : null;
        Require(cliente is not null, T("error.la_cotizacion_no_tiene_cliente"));
        await RequireDescuentoAprobadoAsync(c, null, cotizacion);
        var contacto = cotizacion.ContactoId is { } k
            ? await c.FirstOrDefaultAsync("SELECT * FROM contactos WHERE id = $id", Registros.LeerContacto, null, ("$id", k))
            : null;
        var empresa = await c.FirstOrDefaultAsync("SELECT * FROM company WHERE id = 1", r => new CompanyInfo(r.Str("legal_name"),
            r.StrOrNull("trade_name"), r.StrOrNull("tax_id"), r.Str("country_code"), r.Str("currency_code"), r.StrOrNull("email"),
            r.StrOrNull("phone"), r.StrOrNull("address_line"), r.StrOrNull("city")));
        return (cotizacion, cliente!, contacto, empresa);
    }

    /// <summary>
    /// Líneas con artículo, con el precio como lo usa la suite (con impuesto donde los precios de lista lo incluyen, sin él en
    /// EE. UU.): si la cotización va en el otro modo, el importe se convierte con la tasa del país.
    /// </summary>
    private static List<OrderLine> LineasDelCatalogo(Cotizacion cotizacion) => cotizacion.Lineas
        .Where(l => l.ItemId is not null && l.Cantidad > 0 && !l.Opcional)
        .Select(l =>
        {
            var tasa = cotizacion.TasaImpuesto;
            var importe = (cotizacion.ModoIgv, cotizacion.Regimen.PricesIncludeTax) switch
            {
                (ModoIgv.Adicional, true) => Math.Round(l.Importe * (1 + tasa), 2, MidpointRounding.AwayFromZero),
                (ModoIgv.Incluido, false) => Math.Round(l.Importe / (1 + tasa), 2, MidpointRounding.AwayFromZero),
                _ => l.Importe,
            };
            return new OrderLine(l.ItemId!.Value, l.Cantidad, Math.Round(importe / l.Cantidad, 4, MidpointRounding.AwayFromZero));
        })
        .ToList();
}
