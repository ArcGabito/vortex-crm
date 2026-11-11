# Hoja de ruta

Guía para que Vortex CRM cumpla lo que un equipo comercial exige a un CRM empresarial y compita con Salesforce Sales
Cloud, HubSpot, Pipedrive y Zoho CRM en **Perú, LATAM, EE. UU. y Europa**. Lo común a la suite está en
[ROADMAP-SUITE](https://github.com/ArcGabito/vortex-api/blob/main/docs/ROADMAP-SUITE.md).

Cómo usarla:

1. **Requisitos empresariales** es la lista de lo que debe tener el CRM. Cada requisito tiene un código (`CRM-xx`),
   su estado y la fase que lo cubre.
2. **Fases** dice en qué orden se construyen, qué datos y eventos necesitan y cuándo se dan por terminadas.
3. Al cerrar una fase se actualiza su **Estado** y el de sus requisitos, y se anota en el `CHANGELOG`.

Los tamaños (S, M, L, XL) son relativos entre fases y se vuelven a estimar al empezar cada una.

## Requisitos empresariales

Estados: **Hecho**, **Parcial** (existe una parte; la fase indica qué falta) y **Pendiente**.

### Clientes y contactos

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-01 | Empresas y personas del hub con contactos | Hecho | — |
| CRM-02 | Ficha de 360° con la actividad de la suite (ventas, comprobantes, pagos, despachos) | Hecho | — |
| CRM-03 | Responsable comercial del cliente y reasignación de cartera | Hecho | 2c |
| CRM-04 | Detección y fusión de duplicados | Hecho | 2c |
| CRM-05 | Segmentos y etiquetas, con listas guardadas | Hecho | 5c |
| CRM-06 | Campos personalizados en clientes, oportunidades y cotizaciones | Hecho | 5c |
| CRM-07 | Jerarquía de empresas (matriz y sucursales) | Hecho | 5c |

### Leads

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-10 | Leads con origen, calificación y estado | Hecho | 2 |
| CRM-11 | Conversión de lead a cliente, contacto y oportunidad en un paso | Hecho | 2 |
| CRM-12 | Asignación automática de leads (por turno, territorio o carga) | Hecho: leads por turno o carga; clientes por territorio | 5a, 5b |
| CRM-13 | Captura desde formularios web | Pendiente | 7 |

### Oportunidades y pronóstico

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-20 | Pipeline con monto, cierre estimado y tasa de cierre | Hecho | — |
| CRM-21 | Varios embudos con etapas, probabilidad y colores configurables | Hecho | 2 |
| CRM-22 | Motivos de pérdida predefinidos y competidor | Hecho | 2 |
| CRM-23 | Productos en la oportunidad (monto calculado) | Hecho | 2 |
| CRM-24 | Pronóstico ponderado por mes y por vendedor | Hecho | 2 |
| CRM-25 | Metas y cuotas por vendedor y equipo, con avance | Hecho | 2 |
| CRM-26 | Alertas de oportunidades estancadas | Hecho | 5a |

### Cotizaciones (CPQ)

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-30 | Cotización con impuestos del país, moneda propia, PDF y WhatsApp | Hecho | 1 |
| CRM-31 | Aceptación que genera el pedido en el ERP | Hecho | — |
| CRM-32 | Plantillas de cotización y textos predefinidos | Hecho | 4a |
| CRM-33 | Listas de precios por cliente compartidas con el ERP y el POS | Hecho | 4a |
| CRM-34 | Descuento máximo por rol y aprobación del gerente | Hecho | 4a |
| CRM-35 | Versiones de una cotización | Hecho | 4a |
| CRM-36 | Aceptación en línea con firma (enlace público del hub) | Hecho | 4b |
| CRM-37 | Productos opcionales y validez de la oferta | Hecho | 4a |

### Actividades y comunicación

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-40 | Tareas con responsable y avisos en Android | Hecho | — |
| CRM-41 | Calendario de tareas y reuniones | Hecho | 3 |
| CRM-42 | Recordatorios en Windows, macOS e iOS | Parcial | 3 |
| CRM-43 | Registro de llamadas, correos y WhatsApp en la ficha | Hecho | 3 |
| CRM-44 | Plantillas de correo y WhatsApp | Hecho | 3 |
| CRM-45 | Envío de la cotización por correo con el PDF | Hecho | 3 |
| CRM-46 | Secuencias de seguimiento (varios pasos con plazos) | Hecho | 5a |

### Equipo y control

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-50 | Roles (`vendedor`, `gerente_ventas`, `consulta`, `administrador`) validados en Core | Hecho | 1 |
| CRM-51 | Vendedor responsable: cada uno cambia lo suyo, el gerente todo | Hecho | 1 |
| CRM-52 | Auditoría de etapas, cotizaciones, pedidos y borrados | Hecho | 1 |
| CRM-53 | Territorios y equipos de venta con visibilidad por jerarquía | Hecho | 5b |
| CRM-54 | Comisiones calculadas sobre lo ganado o lo cobrado | Hecho | 5b |
| CRM-55 | Sedes por usuario | Hecho | 2 |

### Automatización y reportes

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-60 | Avance de etapa al enviar y aceptar una cotización | Hecho | — |
| CRM-61 | Reglas (si pasa X, crear tarea, asignar o avisar) | Hecho | 5a |
| CRM-62 | Reportes: embudo por etapa, conversión, ciclo de venta, actividad y ranking | Parcial: panel con pipeline y ganadas | 5 |
| CRM-63 | Tableros por rol y exportación a Excel y PDF | Pendiente | 5 |

### Servicio al cliente

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-70 | Casos con prioridad, responsable y estados | Pendiente | 6 |
| CRM-71 | Acuerdos de nivel de servicio (SLA) con alertas | Pendiente | 6 |
| CRM-72 | Casos desde correo y WhatsApp | Pendiente | 6 |
| CRM-73 | Encuesta de satisfacción al cerrar | Pendiente | 6 |

### Marketing y privacidad

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-80 | Campañas por correo y WhatsApp a un segmento | Pendiente | 7 |
| CRM-81 | Origen y campaña de cada lead y oportunidad (atribución) | Pendiente | 7 |
| CRM-82 | Consentimientos y baja de comunicaciones | Pendiente | 7 |
| CRM-83 | Exportar o borrar los datos de una persona (Ley 29733, LGPD, GDPR) | Pendiente | 7 |

### Integraciones

| # | Requisito | Estado | Fase |
|---|---|---|---|
| CRM-90 | Correo y calendario de Microsoft 365 y Google | Pendiente | 8 |
| CRM-91 | WhatsApp Business API | Pendiente | 8 |
| CRM-92 | Importación masiva de clientes, contactos y oportunidades | Pendiente | 8 |
| CRM-93 | API y webhooks propios del CRM sobre los del hub | Pendiente | 8 |

## Reglas que el plan respeta

- El cliente es del hub; los datos comerciales son del CRM y se replican con `crm.record.*` (el último cambio gana).
- Nada fijo para un país: impuestos, monedas y formatos vienen de `VortexApi.Suite`.
- Sin conexión primero: los vendedores trabajan en campo desde el celular.
- Un pedido nace en el CRM con Id determinista compartido con el ERP; el CRM no factura ni mueve stock.
- Los datos personales se tratan con consentimiento y se pueden exportar o borrar.
- Módulos opcionales por `features` del hub (`leads`, `automatizacion`, `servicio`, `marketing`…).

## Fase 1: base común de la suite · M

Impuestos por país y moneda en cotizaciones, roles del CRM con vendedor responsable, auditoría e idiomas (español,
inglés y portugués, también en el PDF y el WhatsApp de la cotización).

**Terminado cuando:** una cotización para Colombia sale con IVA 19 % en pesos colombianos y un `vendedor` no puede
borrar la oportunidad de otro.

**Estado (2026-11):** hecha.

## Fase 2: embudos, leads y pronóstico · L

**Objetivo:** que el gerente sepa cuánto va a vender este mes y de dónde sale.

- CRM-21, CRM-22: embudos configurables por línea de negocio (etapas, probabilidad, colores) y motivos de pérdida
  predefinidos. Las cinco etapas actuales pasan a ser el embudo por defecto.
- CRM-10, CRM-11: leads con origen y conversión.
- CRM-23 a CRM-25: productos en la oportunidad, pronóstico ponderado y metas.
- CRM-03, CRM-04: responsable del cliente, reasignación de cartera y duplicados.
- CRM-55: sedes por usuario (`Access.SiteIds`, ya en el hub).

**Datos y eventos:** embudos, leads y metas viajan como `crm.record.upserted`. `deal.won` gana `pipelineId` y
`lines` (aditivos).

**Hub:** responsable comercial y segmento del cliente en `party`.

**Terminado cuando:** dos embudos con etapas distintas conviven; convertir un lead crea cliente, contacto y
oportunidad; el pronóstico del mes coincide en dos equipos.

**Estado (2026-11):** 2a hecha: embudos configurables (etapas con probabilidad, color y equivalencia al ciclo común,
embudo por defecto y archivado), motivos de pérdida y competidor, metas por vendedor y equipo, pronóstico ponderado
de los próximos meses y avance por vendedor, y sedes por usuario. Pruebas en `EmbudosTests`. 
**Estado (2026-11), 2b:** hecha. Leads con origen, estado (nuevo, contactado, calificado, descartado con motivo,
convertido) y responsable, replicados como `crm.record.upserted` (`lead`); la conversión crea en una transacción el
cliente (o usa el que ya tiene su documento), el contacto y, si se pide, la oportunidad. Productos en la oportunidad: el
monto es su suma y `deal.won` lleva `pipelineId` y `lines`. Pruebas en `LeadsTests`.
**Estado (2026-11), 2c:** hecha. Responsable del cliente en el `party` del hub (bloque `account`): un vendedor toma un
cliente libre o suelta uno suyo; el gerente asigna a cualquiera y reasigna la cartera de un vendedor (clientes y sus
oportunidades abiertas). Filtros "Mis clientes" y "Sin responsable". Duplicados por documento, nombre normalizado (sin
tildes ni S.A.C.), correo o teléfono, sin juntar documentos distintos; fusionar pasa contactos, oportunidades,
cotizaciones, tareas y leads al que se queda y desactiva el otro en el hub con una nota. Pruebas en `CarteraTests`.
El segmento del cliente queda para la fase 5 (CRM-05).

## Fase 3: actividades y comunicación · M

- CRM-41, CRM-42: calendario de tareas y reuniones, recordatorios en Windows, macOS e iOS.
- CRM-43 a CRM-45: registro de llamadas, correos y WhatsApp; plantillas; envío de la cotización por correo.

**Terminado cuando:** un vendedor agenda una visita desde el celular, recibe el aviso en el escritorio y registra el
resultado en la ficha.

**Estado (2026-11):** hecha. Interacciones (llamada, WhatsApp, correo, reunión, visita, nota) con resumen y resultado en
la ficha del cliente, replicadas a todo el equipo; cada vendedor corrige o borra las suyas. Al completar una tarea con
cliente u oportunidad se anota qué pasó y queda como interacción del tipo de la tarea. Vista semanal de tareas (en el
celular los días van uno debajo del otro). Plantillas de WhatsApp y correo con variables (`{cliente}`, `{contacto}`,
`{vendedor}`, `{empresa}`, `{cotizacion}`, `{monto}`, `{fecha}`) que configura el gerente; enviar con una plantilla
registra la interacción. La cotización se envía por correo con el PDF adjunto (cliente de correo del equipo; si no hay,
se exporta el PDF y se abre el correo). Avisos de tareas en Windows con notificaciones del sistema mientras la app está
abierta (también minimizada); macOS e iOS quedan para cuando haya esas plataformas (CRM-42 parcial). Pruebas en
`ActividadesTests`.

## Fase 4: cotizaciones empresariales · M

- CRM-32 a CRM-37: plantillas, listas de precios compartidas, descuento máximo por rol con aprobación, versiones,
  firma en línea, productos opcionales y validez.

**Hub:** listas de precios (las mismas del ERP y el POS); enlace público firmado para aceptar cotizaciones.

**Estado (2026-11):** 4a hecha. El precio de cada línea sale de la lista de precios del hub (`price_list`) que rige: la
del cliente (`credit.priceListId`) o, si no tiene, la de la sede, vigente hoy y en la moneda de la cotización, con sus
escalas por cantidad; sin lista, el del artículo. El precio de lista queda en la línea y contra él se mide el descuento;
al cambiar cliente, cantidad, moneda, impuesto o sede se vuelve a calcular (lo puesto a mano se conserva). El gerente fija
en los ajustes el descuento máximo del vendedor: una cotización que lo pasa se guarda, pero no se envía (ni por WhatsApp
o correo) hasta que un gerente apruebe ese descuento; si después se baja más, hay que volver a aprobar. Filtro "Por
aprobar" para el gerente. Versiones: desde una cotización enviada o rechazada se crea la v2, v3… con el mismo número, y la
anterior queda Reemplazada (el pedido de una versión lleva `-V2`). Líneas opcionales: se muestran aparte en la pantalla,
el PDF y WhatsApp, no suman al total ni pasan al pedido. Plantillas de cotización compartidas por el equipo (líneas,
condiciones, impuesto y validez) que se llenan con el precio de lista de hoy. Pruebas en
`CotizacionesEmpresarialesTests` y `SyncTests`.

4b hecha (2026-11): "Crear enlace para aceptar" sube al hub una copia de la cotización (`api/public-links`) y la deja
Enviada; el enlace se copia y va en el mensaje de WhatsApp y del correo. El cliente la ve en `/p/{token}` (en el idioma
del vendedor) y la acepta con nombre y firma dibujada, o la rechaza. La respuesta llega por el feed
(`hub.public_link.accepted|rejected`): la cotización queda Aceptada con el firmante, gana su oportunidad, publica
`quote.accepted` y genera el pedido (`order.created`) si todas sus líneas son del catálogo; o queda Rechazada con el
comentario. En los dos casos queda una nota en la ficha del cliente. La pantalla muestra cuántas veces abrió el enlace y,
tras la respuesta, la firma. Editar la cotización y volver a pedir el enlace actualiza la copia (misma URL); una versión
nueva retira el enlace anterior. Pruebas en `AceptacionEnLineaTests`. Fase 4 terminada.

**Terminado cuando:** una cotización con un descuento mayor al permitido no se puede enviar sin la aprobación del
gerente; el cliente la acepta desde el enlace y el pedido llega al ERP.

## Fase 5: equipo, automatización y reportes · L

- CRM-53, CRM-54: territorios, visibilidad por jerarquía y comisiones.
- CRM-12, CRM-26, CRM-46, CRM-61: asignación automática de leads, alertas de estancadas, secuencias y reglas.
- CRM-05 a CRM-07: segmentos, campos personalizados y jerarquía de empresas.
- CRM-62, CRM-63: reportes y tableros por rol con exportación.

**Multiequipo:** las reglas se ejecutan en un solo equipo por registro (el del responsable), para no crear tareas
repetidas.

**Terminado cuando:** una oportunidad quieta 15 días crea una tarea a su responsable una sola vez aunque haya varios
equipos abiertos.

**Estado (2026-11):** 5a (automatización) hecha. Reglas configurables por el gerente (Configuración → Automatización):
oportunidad sin movimiento, cotización enviada sin respuesta o por vencer, lead nuevo sin contactar y oportunidad ganada
(posventa), con días; la acción crea una tarea al responsable (título con `{registro}`, tipo y plazo) o inicia una
secuencia. Sin reglas guardadas rige la predeterminada: oportunidad quieta 15 días → tarea. Secuencias de seguimiento
(pasos con día, tipo y título) que se inician sobre una oportunidad o un lead y se detienen solas al cerrarse. Reparto de
leads nuevos sin responsable por turno o por carga entre los vendedores elegidos. Las reglas corren tras cada
sincronización (cada 10 minutos como mucho, también sin conexión) en los equipos del responsable; cada disparo es un
registro replicado con Id determinista, igual que la tarea que crea, así no se repite aunque haya varios equipos o el
vendedor la borre. Pruebas en `AutomatizacionTests`. Siguen: 5b (territorios, visibilidad por jerarquía y comisiones),
5c (segmentos, campos personalizados y jerarquía de empresas) y 5d (reportes y tableros por rol).

**Estado 5b (2026-11):** hecha. Pantalla Equipo: **equipos de venta** con líder, vendedores, **territorio** (ciudades,
regiones o distritos) y equipo del que dependen. "Asignar clientes por territorio" da los clientes sin responsable al
vendedor del equipo de su zona con menos clientes. **Visibilidad**: por defecto todos ven todo (como antes); por
jerarquía, cada vendedor ve sus oportunidades, cotizaciones y leads, el líder los de su equipo y los de abajo, y el
gerente todo (lo que no tiene responsable lo ven todos). **Comisiones** del mes por vendedor sobre lo ganado o lo cobrado
a su cartera, con tasa general o por vendedor y un acelerador para lo que pasa la meta; cada uno ve la suya, el líder las
de su equipo; exportación CSV. Registros nuevos `equipo`, `visibilidad` y `plan_comisiones`. Pruebas en `EquiposTests`.

**Estado 5c (2026-11):** hecha. **Etiquetas** por cliente (minúsculas, hasta 20) y **segmentos** en Clientes: búsqueda,
cartera, etiquetas (cualquiera o todas), ciudad, sin actividad en 30/60/90/180 días y valor de un campo personalizado;
el filtro se guarda con nombre, privado o compartido, y se exporta a CSV (protegido contra fórmulas). **Campos
personalizados** (texto, número, fecha, lista, sí/no; obligatorios o no) que el gerente define en Configuración para
clientes, oportunidades y cotizaciones; los valores se validan y normalizan por tipo. **Jerarquía de empresas**: matriz
y sucursales del cliente (sin ciclos, hasta 5 niveles) en la ficha. Todo son registros del CRM: `perfil_cliente`,
`campo_personalizado`, `valores_campos` y `lista_guardada`; el cliente sigue siendo el party del hub. Pruebas en
`SegmentosTests`. Sigue 5d (reportes y tableros por rol).

## Fase 6: servicio al cliente · L

- CRM-70 a CRM-73: casos con SLA, desde correo y WhatsApp, y encuesta al cerrar. Se activa con el `feature`
  `servicio`.

**Terminado cuando:** un caso que vence su SLA avisa al supervisor y queda en el reporte de cumplimiento.

## Fase 7: marketing y privacidad · M

- CRM-80, CRM-81: campañas a segmentos y atribución.
- CRM-82, CRM-83: consentimientos, baja de comunicaciones y exportación o borrado de datos personales.
- CRM-13: formularios web para capturar leads (vía hub).

**Hub:** consentimientos del cliente en `party` (compartidos con el POS); endpoint público para formularios.

**Terminado cuando:** una persona que se dio de baja no recibe campañas, y su solicitud de borrado elimina sus datos
del CRM en todos los equipos.

## Fase 8: integraciones · M

CRM-90 a CRM-93: correo y calendario externos, WhatsApp Business, importación masiva y API.

## Propuestas al hub

| Propuesta | Tipo | Fase |
|---|---|---|
| Responsable comercial y segmento del cliente | Campos de `party` | 2 |
| `pipelineId` y `lines` en `deal.won` | Campos aditivos | 2 |
| Listas de precios | Dato maestro compartido con ERP y POS | 4 |
| Enlace público firmado para aceptar cotizaciones | Endpoint | 4 |
| Consentimientos del cliente | Campos de `party` | 7 |
| Formularios web para leads | Endpoint público | 7 |

## Decisiones pendientes

| # | Decisión | Recomendación | Antes de |
|---|---|---|---|
| 1 | Leads en el CRM o como `party` del hub | En el CRM hasta convertirse; al convertir se crea el `party` | Fase 2 |
| 2 | Qué equipo ejecuta las reglas automáticas | El del responsable del registro; si no está activo, el primero que sincroniza después del plazo | Fase 5 |
| 3 | Servicio al cliente en el CRM o en otro sistema | En el CRM con el `feature` `servicio` | Fase 6 |
| 4 | Envío de correo y WhatsApp masivo | Proveedor externo (SMTP transaccional, WhatsApp Business API) configurado en el hub | Fase 7 |

## Versiones

| Versión | Contenido |
|---|---|
| 0.2.0 | Fase 1 |
| 0.3.0 | Fase 2 |
| 0.4.0 | Fase 3 |
| 0.5.0 | Fase 4 |
| 0.6.0 | Fase 5 |
| 1.0.0 | Estabilización y piloto con un equipo comercial real |
| 1.x | Fases 6, 7 y 8 |

## Cómo se trabaja cada fase

1. **Hub primero:** las propuestas de la fase se envían a `vortex-api` con pruebas y se vuelve a empaquetar el SDK
   (`scripts/pack-sdk.*`).
2. **Base de datos:** una migración nueva en `CrmDatabase`; nunca se edita una publicada.
3. **Registros:** los datos comerciales nuevos se replican con `crm.record.*`; los pedidos usan Id determinista.
4. **Permisos y auditoría:** cada acción sensible valida su permiso en Core y deja su auditoría.
5. **Pruebas:** unitarias y un escenario en `SyncTests` por cada dato replicado.
6. **Documentación:** los eventos nuevos o ampliados van en [INTEGRACION.md](INTEGRACION.md); los cambios, en el
   `CHANGELOG`; los requisitos cumplidos se marcan en esta guía.
7. **Pantallas:** con los componentes compartidos, en los tres idiomas, probadas en Windows y en la vista móvil.
