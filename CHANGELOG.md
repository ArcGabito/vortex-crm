# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
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
