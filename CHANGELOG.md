# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
- **Automatización (fase 5a):** reglas que crean tareas o inician secuencias (oportunidad sin movimiento, cotización sin
  respuesta o por vencer, lead sin contactar, posventa), con la de oportunidad quieta 15 días activa por defecto;
  secuencias de seguimiento que se detienen solas al cerrarse la oportunidad o el lead; reparto de leads nuevos por turno
  o por carga. Cada regla salta una sola vez aunque el vendedor tenga varios equipos. Pantalla Automatización.
- **Aceptación en línea (fase 4b):** enlace público del hub para que el cliente revise la cotización y la acepte con su
  firma o la rechace, sin usuario. Al aceptarla, la cotización queda Aceptada con el firmante, gana su oportunidad y el
  pedido llega al ERP solo. El enlace va en el mensaje de WhatsApp y del correo; la pantalla muestra si el cliente lo
  abrió y su firma.
- **Cotizaciones empresariales (fase 4a):** precio de lista desde las listas de precios del hub (la del cliente o la de
  la sede, con escalas por cantidad) y descuento de cada línea contra ese precio. Descuento máximo del vendedor en los
  ajustes: la cotización que lo pasa no se envía sin la aprobación del gerente (filtro "Por aprobar"). Versiones de una
  cotización (v2, v3…; la anterior queda Reemplazada). Productos opcionales que se ofrecen aparte y no suman ni pasan al
  pedido. Plantillas de cotización compartidas por el equipo.
- **Actividades (fase 3):** interacciones con el cliente (llamada, WhatsApp, correo, reunión, visita, nota) con su
  resultado en la ficha; completar una tarea pide qué pasó y lo deja como interacción. Vista semanal de tareas.
  Plantillas de WhatsApp y correo con datos del cliente, configurables por el gerente. Envío de la cotización por
  correo con el PDF adjunto. Avisos de tareas en Windows mientras la app está abierta.
- **Cartera (fase 2c):** responsable del cliente compartido por el hub (`party.account`): el vendedor toma clientes
  libres; el gerente asigna y reasigna carteras con sus oportunidades abiertas; filtros "Mis clientes" y "Sin
  responsable". Detección de clientes duplicados (documento, nombre, correo, teléfono) y fusión: lo del duplicado pasa
  al que se queda y el duplicado queda inactivo en el hub.
- **Leads (fase 2b):** interesados con origen (web, referido, redes, evento, llamada), estado y responsable; se
  califican, se descartan con motivo y se convierten en un paso en cliente (o se unen al que ya tiene su documento),
  contacto y oportunidad. Pantalla Leads.
- **Productos en la oportunidad:** artículos del catálogo con cantidad y precio; el monto es su suma y `deal.won` los
  lleva (`lines`) junto con el embudo (`pipelineId`).
- La anulación de un comprobante en el ERP (`invoice.voided`) queda en la actividad del cliente con su motivo y descuenta
  su monto de "Facturado".
- **Celular (Android):** la app respeta las barras del sistema (estado y gestos) con íconos oscuros; el botón Atrás
  cierra el diálogo o el menú abiertos y vuelve a la página anterior en vez de cerrar la app; las tablas se ven como
  tarjetas y las pestañas que no caben se deslizan; en pantallas táctiles no se abre el teclado solo. El negro de la
  pantalla de inicio, el ícono, la barra de título y el logo deja el café `#1C120D` que se veía amarillento; los montos
  menores a 10 mil del panel se muestran con decimales. Corrige la compilación para Android (`Android.Net` y `Android.Graphics` se resolvían dentro del espacio de nombres `VortexCrm.Platforms.Android`).
- **Embudos de venta configurables (fase 2a):** uno por línea de negocio, con sus etapas, probabilidad de cierre y
  color; cada etapa equivale a Prospecto, Cotizado o Negociación, así las cotizaciones siguen haciendo avanzar la
  oportunidad. Pantalla Embudos (desde Configuración) y tablero del pipeline por embudo.
- **Motivos de pérdida** configurables y **competidor** de cada oportunidad.
- **Pronóstico y metas:** lo ganado y lo ponderado por probabilidad de los próximos meses contra la meta del equipo, y
  el avance de cada vendedor en el mes. Metas por mes, de un vendedor o del equipo.
- **Sedes por usuario:** con sedes asignadas en el hub, las cotizaciones solo salen de esas sedes.
- **Microsoft Store:** paquete MSIX generado por `.github/workflows/store.yml` (etiqueta `vX.Y.Z`), firmado por la Store
  para instalar sin la advertencia de SmartScreen; envío automático opcional. Guía en `docs/MICROSOFT-STORE.md`.
- **El idioma sigue al usuario:** el que elige en Configuración se guarda en el hub y se usa al entrar en cualquier
  sistema o equipo de la suite. Sin conexión queda en este equipo.
- **Idiomas**: la interfaz en español, inglés y portugués. Se elige en Configuración → Este equipo; por defecto sale
  del país de la empresa (portugués en Brasil y Portugal, inglés en EE. UU. y el Reino Unido, español en el resto).
  Una prueba verifica que cada texto exista en los tres idiomas con los mismos datos variables.
- Los **mensajes de validación y de permisos**, el PDF y el mensaje de WhatsApp de la cotización, los avisos de las tareas salen en el idioma elegido (`CoreText` en Core,
  con el mismo catálogo que la interfaz).
- **Base común de la suite** (`VortexApi.Suite`): las cotizaciones usan el impuesto del país de la empresa (IGV, IVA,
  VAT…) con su tasa y nombre en pantalla, PDF y WhatsApp, y tienen moneda propia (redondeo según la moneda y monto en
  letras en esa moneda). Formatos y hora según el país de la empresa.
- **Roles del CRM** asignados en el hub: `vendedor` (por defecto), `gerente_ventas`, `consulta` y `administrador`.
  Oportunidades y cotizaciones tienen **vendedor responsable**: cada vendedor cambia lo suyo y el gerente, todo.
- **Auditoría** de cambios de etapa, estados de cotizaciones, pedidos, ajustes y eliminaciones (pantalla Auditoría).
- `docs/ROADMAP.md`: hoja de ruta para competir con Salesforce, HubSpot, Pipedrive y Zoho CRM.
- Integración con el hub `vortex-api`: login sin usuarios propios, clientes, productos, sedes y datos de la empresa
  compartidos con la suite, y sincronización sin conexión.
- Varios equipos de la misma organización comparten contactos, oportunidades, cotizaciones, tareas y ajustes
  (`crm.record.upserted` / `crm.record.deleted`, el último cambio gana).
- Eventos `quote.accepted`, `deal.won` y `order.created` (Id determinista compartido con el ERP); actividad del cliente
  con las ventas del POS, comprobantes y pagos del ERP y despachos del WMS.
- Ficha del cliente de 360° y panel con pipeline, ventas ganadas, cotizaciones abiertas y tareas.
- Tareas con responsable: los avisos de Android solo suenan en sus equipos y se reprograman al abrir la app.
- CI (pruebas en Linux y app en Windows), Dependabot, plantilla de pull request, CONTRIBUTING, SECURITY y CLAUDE.md.

### Changed
- `docs/ROADMAP.md` pasa a ser la guía de requisitos empresariales: cada requisito (`CRM-xx`) con su estado y
  la fase que lo cubre, y cada fase con su criterio de terminado, propuestas al hub y decisiones pendientes.
- **Morado .NET como color de la suite:** el naranja de la marca (`#FF5B14`) pasa a `#512BD4`, el morado de .NET,
  en el acento de `app.css`, el logo, el ícono de la app, la pantalla de inicio, la barra de título y Android. La portada
  del inicio de sesión se muestra en tonos morados con `filter: hue-rotate`. Los colores de estado no cambian.
  También el color de las cotizaciones en PDF y el de la notificación de recordatorios en Android.
- Arquitectura de Vortex WMS: `VortexCrm` (MAUI Blazor Hybrid) + `VortexCrm.Core` + pruebas, con el sistema de diseño
  de la suite en lugar de MudBlazor y SQLite sin ORM en lugar de EF Core.
- Cotizaciones numeradas por equipo (`COT-3F0C-0012`) y PDF con los datos de la empresa y la sede del hub.
- PDFsharp-MigraDoc con `System.Security.Cryptography.Pkcs` 10.0.14 (la versión que traía tenía vulnerabilidades).

### Removed
- Modo servidor (`Vortex.Api` con SQL Server), host web (`Vortex.Web`) y la configuración protegida con clave de
  administrador: los cuatro sistemas de la suite usan solamente el hub.
- Consulta de RUC/DNI de prueba y catálogo de ubigeos del INEI.
- Workflow de publicación para Windows anterior.

### Fixed
- El buscador de artículos perdía dígitos cuando un lector de códigos escribía rápido.
- Celular: los diálogos y los indicadores ya no se salen de la pantalla; el editor de líneas pasa a dos filas.
