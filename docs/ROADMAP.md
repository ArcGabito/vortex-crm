# Hoja de ruta

Plan para que Vortex CRM compita con Salesforce Sales Cloud, HubSpot, Pipedrive y Zoho CRM en equipos comerciales de
**Perú, LATAM, EE. UU. y Europa**. Las fases van en el orden en que conviene construirlas; la fase 1 es común a la
suite ([ROADMAP-SUITE](https://github.com/ArcGabito/vortex-api/blob/main/docs/ROADMAP-SUITE.md)).
Tamaños (S, M, L, XL) relativos entre fases.

## Dónde estamos (0.1.0)

| Área | Hoy | Falta para competir |
|---|---|---|
| Clientes | Empresas y personas del hub, contactos, ficha de 360° con actividad de la suite | Leads y conversión, segmentación, campos personalizados, duplicados |
| Pipeline | Cinco etapas fijas, monto, cierre estimado, tasa de cierre | Varios embudos con etapas configurables y probabilidad, motivos de pérdida, pronóstico |
| Cotizaciones | Con IGV, PDF, WhatsApp, aceptación y pedido | Impuestos por país, monedas, plantillas, descuentos con aprobación, firma |
| Actividades | Tareas con responsable y avisos en Android | Calendario, correo y llamadas registrados, recordatorios en escritorio |
| Equipo | Varios equipos sincronizados | Roles, territorios, cartera por vendedor, metas y comisiones |
| Reportes | Panel con pipeline y ventas ganadas | Embudo por etapa, actividad por vendedor, previsión, exportación |
| Automatización | Avance de etapa al enviar y aceptar | Reglas (si pasa X, crear tarea o avisar), secuencias de seguimiento |
| Internacional | Perú, soles, español | Impuestos por país, monedas, idiomas |

## Reglas que el plan respeta

- El cliente es del hub; los datos comerciales son del CRM y se replican con `crm.record.*` (último cambio gana).
- Nada fijo para un país: impuestos, monedas y formatos vienen de `VortexApi.Suite`.
- Sin conexión primero; los vendedores trabajan en campo desde el celular.
- Módulos opcionales por `features` del hub (`leads`, `automatizacion`, `metas`…).

## Fase 1: base común de la suite · M

- **Impuestos por país** en cotizaciones (reemplaza el IGV fijo) y **moneda de la cotización**.
- **Roles del CRM**: `administrador`, `gerente_ventas`, `vendedor`, `consulta`. Un vendedor ve y edita lo suyo; el
  gerente ve todo el equipo.
- **Auditoría** de cambios de etapa, aceptaciones, descuentos y borrados.
- **Idiomas** español, inglés y portugués (1b, hecho): la interfaz completa, con selector en Configuración → Este
  equipo (por defecto, el idioma del país de la empresa). Los mensajes de validación, el
  PDF y el WhatsApp de la cotización también salen en el idioma elegido.

**Terminado cuando:** una cotización para Colombia sale con IVA 19 % en pesos colombianos y un `vendedor` no puede
borrar la oportunidad de otro.

## Fase 2: embudos, leads y pronóstico · L

- Embudos configurables (etapas, probabilidad, colores) por línea de negocio; motivos de pérdida predefinidos.
- Leads (prospectos sin calificar) con origen y conversión a cliente, contacto y oportunidad.
- Pronóstico ponderado por probabilidad y por mes; metas por vendedor y avance.
- Responsable de cada cliente y oportunidad; reasignación de cartera.

## Fase 3: actividades y comunicación · M

- Calendario de tareas y reuniones; recordatorios en Windows y macOS.
- Registro de llamadas, correos y mensajes de WhatsApp en la ficha.
- Plantillas de correo y de WhatsApp; envío de la cotización por correo con el PDF.

## Fase 4: cotizaciones empresariales · M

- Plantillas de cotización y textos predefinidos; listas de precios por cliente (del ERP).
- Descuento máximo por rol y aprobación del gerente; versiones de una misma cotización.
- Aceptación en línea con firma (enlace público vía hub).

## Fase 5: automatización y reportes · L

- Reglas de automatización (crear tarea al pasar a una etapa, avisar si una oportunidad lleva N días quieta).
- Reportes: embudo por etapa, conversión, ciclo de venta, actividad y ranking por vendedor; exportación.
- Campos personalizados en clientes, oportunidades y cotizaciones.

## Fase 6: integraciones · M

Correo (Microsoft 365, Google), WhatsApp Business, formularios web para capturar leads, calendario externo.

## Propuestas al hub

Tipos de cambio como dato maestro; responsable comercial y segmento del cliente (`party`); enlace público firmado
para aceptar cotizaciones.
