# Integración del CRM con el hub y la suite

Vortex CRM sigue la guía del hub ([`vortex-api/docs/SISTEMAS.md`](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md)).
Este documento resume qué hace el CRM con cada dato y evento, y cómo trabajan juntos varios vendedores sin conexión.

## Identidad y sesión

- No hay tabla de usuarios ni clave de administrador propia: el login es contra el hub (`ProductCode = "CRM"`, clave de
  sistema en `appsettings.json` o en la pantalla de ingreso). Los tokens se guardan en `SecureStorage`.
- Cada organización tiene su propia base SQLite (`vortex-crm-{organizationId}.db` en `AppDataDirectory`).

## Datos maestros (dueño: el hub)

| Dato | En el CRM | Altas/cambios desde el CRM |
|---|---|---|
| Clientes (`party`) | Empresas y personas a las que se vende; su dirección va en la cotización | Alta |
| Artículos (`item`) | Productos y servicios para cotizar | Sí |
| Sedes (`site`) | Sede que emite la cotización (su dirección va en el PDF) | Alta |
| Empresa (`company`) | Nombre, RUC, dirección, teléfono y correo del encabezado del PDF; moneda | No |

## Datos propios del CRM

Contactos, oportunidades, cotizaciones (con sus líneas), tareas y ajustes de ventas (IGV y condiciones por defecto).
Se guardan en la base local y se replican a los demás equipos de la organización con dos eventos del catálogo del hub:

| Evento | Contenido |
|---|---|
| `crm.record.upserted` | `{ kind, id, updatedAt, data }`: la versión completa del registro (`contacto`, `oportunidad`, `cotizacion`, `tarea`, `ajustes`) |
| `crm.record.deleted` | `{ kind, id, updatedAt }` |

- Cada equipo aplica lo que llega solo si es **más nuevo** que lo que tiene (`actualizado_en`): el último cambio gana y
  un evento repetido o atrasado no pisa nada.
- Un borrado no elimina un registro que el otro equipo editó después.
- Las cotizaciones se numeran por equipo (`COT-3F0C-0012`) para no chocar sin conexión.
- Las tareas tienen responsable: sus avisos solo se programan en los equipos de esa persona (también cuando la tarea
  llega de otro equipo, y de nuevo al abrir la app, porque Android borra las alarmas al reiniciar).

## Impuestos, monedas y permisos (base común de la suite)

- La cotización usa el impuesto del país de la empresa (`TaxCatalog.For(country, defaultTaxRate)`): su nombre y tasa
  aparecen en pantalla, en el PDF y en WhatsApp. Tiene moneda propia (por defecto la de la empresa) y se redondea con
  sus decimales. `quote.accepted` y `order.created` viajan en esa moneda.
- `crm.record.upserted` agrega `moneda`, `responsableId` y `responsableNombre` en cotizaciones y oportunidades (los
  eventos anteriores, sin esos campos, se aplican igual).
- Roles del CRM (`session.Access.Role`): `vendedor` (sin rol asignado; trabaja lo suyo), `gerente_ventas` (edita lo de
  todos, elimina, cambia los ajustes y ve la auditoría), `consulta` (solo ve) y `administrador` (todo, también productos
  y sedes). OrgAdmin y PlatformAdmin del hub son administradores.

## Eventos de la suite (versión 1)

### Qué publica el CRM

| Evento | Cuándo | Id del evento |
|---|---|---|
| `quote.accepted` | Al marcar una cotización como aceptada | `DeterministicId("quote_accepted", quoteId)` |
| `deal.won` | Al ganar una oportunidad (a mano o al aceptar su cotización) | `DeterministicId("deal_won", dealId)` |
| `order.created` | Al generar el pedido de una cotización aceptada | `DeterministicId("order_from_quote", quoteId)` |

- Las líneas de `quote.accepted` y `order.created` son solo las del catálogo (las líneas libres no se pueden despachar)
  y su precio va **con IGV** como el precio de lista de la suite, aunque la cotización sea "más IGV".
- `order.created` usa el mismo Id que calcula el ERP al convertir la cotización: si los dos generan el pedido, el hub
  recibe uno solo y el WMS reserva una sola vez. Extiende v1 con `quoteId`.
- `quote.accepted` extiende v1 con `number`.

### Qué consume el CRM

| Evento | Efecto |
|---|---|
| `sale.completed` (POS) | Compra en caja en la actividad del cliente (si la venta tiene cliente) |
| `invoice.issued` (ERP) | Comprobante en la actividad del cliente (y en "Facturado" de su ficha) |
| `payment.received` (ERP) | Pago en la actividad del cliente (y en "Pagado") |
| `shipment.dispatched` (WMS) | Despacho con transportista y guía en la actividad del cliente |

Cada evento se aplica una sola vez (`processed_events`); `schemaVersion` distinto de 1 o `data` ilegible se registran
como ignorados y no traban el feed.

## Cambios respecto de la versión anterior del CRM

- No hay "modo servidor" ni API propia: como los cuatro sistemas de la suite, el CRM usa solamente el hub, y los
  equipos se sincronizan entre sí a través de él.
- Las empresas y las personas cliente son *parties* del hub; los contactos son personas dentro de un cliente.
- El catálogo de productos, las sucursales y los datos del negocio vienen del hub. Los ajustes de ventas (IGV y
  condiciones por defecto) siguen siendo del CRM.
- Sin migración automática: la base local de la versión anterior (`vortex.db`, EF Core) no se importa.

### Limitaciones conocidas

- La consulta de RUC/DNI y el catálogo de ubigeos del INEI se retiraron; la dirección se escribe a mano.
- No hay versión web (Blazor Web App): la suite usa la app MAUI Blazor Hybrid en Windows, Android, iOS y macOS.
