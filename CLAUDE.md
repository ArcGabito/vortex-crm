# CLAUDE.md

Vortex CRM: gestión comercial de la suite Vortex (ERP, CRM, WMS, POS). App .NET MAUI Blazor Hybrid, AGPL-3.0, textos
de la interfaz y documentación en español (es-PE). Sigue la misma arquitectura que Vortex WMS (la referencia).

## Comandos

```bash
dotnet test tests/VortexCrm.Core.Tests                                   # dominio, servicios y sincronización
VORTEX_HUB_URL=http://localhost:5299/ dotnet test tests/VortexCrm.Core.Tests   # + prueba contra un hub real
dotnet build src/VortexCrm -f net10.0-windows10.0.19041.0                # app en Windows (también -f net10.0-android)
dotnet run --project src/VortexCrm -f net10.0-windows10.0.19041.0        # compila y abre (en Windows, -t:Run no compila)
scripts/pack-sdk.sh   |   ./scripts/pack-sdk.ps1                         # SDK del hub -> packages/vortex-sdk
```

## Estructura

- `src/VortexCrm.Core`: todo lo que no es interfaz. `Domain/` (reglas: `Cotizacion`, `Oportunidad`, `Tarea`,
  `FormatoPeru`, `MontoEnLetras`…), `Data/` (SQLite sin ORM, migraciones en `CrmDatabase`; `Registros` guarda y replica
  los datos del CRM), `Services/` (`ServicioCotizaciones`, `ServicioOportunidades`, `ServicioTareas`, `ServicioContactos`,
  `ServicioClientes`, `MasterDataService`), `Pdf/`, `Sync/`, `Integration/` (eventos v1).
- `src/VortexCrm`: app. `Services/AppSession.cs`, `Components/Pages` (heredan `CrmPage`), `Components/Shared`
  (incluye `OportunidadModal`, `TareaModal`, `ContactoModal`), `Platforms/Android/ServicioRecordatoriosAndroid.cs`.
- `tests/VortexCrm.Core.Tests`: xUnit; `Dominio/` (reglas), `VentasTests`, `SyncTests` (otro equipo y otros sistemas).

## Reglas

- El cliente es un *party* del hub (`ClienteId`); contactos, oportunidades, cotizaciones, tareas y ajustes son del CRM.
- Todo cambio de un dato del CRM pasa por `Registros.PublicarAsync` (guarda + `crm.record.upserted` en la misma
  transacción). Lo que llega de otro equipo gana solo si es más nuevo (`actualizado_en`).
- Numeración por equipo (`COT-3F0C-0012`). Pedido de una cotización: `ServicioCotizaciones.PedidoIdPara` (= ERP).
- Las líneas que viajan a la suite (`quote.accepted`, `order.created`) son solo las del catálogo, con precio con IGV.
- Errores para el usuario: `CrmValidationException` con el mensaje final en español.
- En Razor, dentro de atributos usa `@(...)` cuando haya comillas y no nombres variables `code` ni `page`.

## Hub Vortex (identidad, datos maestros e integración)

Este sistema es **Vortex CRM** (`ProductCode = "CRM"`) y depende del hub `vortex-api`, clonado en `~/vortex-api`.
Antes de tocar login, sesiones, usuarios, clientes, artículos, sedes, datos de la empresa o comunicación con otro
sistema de la suite, lee `~/vortex-api/docs/SISTEMAS.md` y síguelo (`docs/INTEGRACION.md` resume cómo lo aplica el CRM).

- Levantar el hub: `cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http`
  (http://localhost:5299, referencia en /docs). Usuario demo: `luis@vortex.local` / `Suite-Local-2026!`,
  clave de sistema `vxp_crm_dev_key_change_me`.
- SDK: paquete `VortexApi.Client` desde el feed local `packages/vortex-sdk` (`scripts/pack-sdk.*`). Si cambias el
  SDK, hazlo en `~/vortex-api` con sus pruebas y vuelve a empaquetar.
- No crear tablas de usuarios ni contraseñas; no llamar directamente a otros sistemas; usar Ids del hub.
- Si falta algo en el hub, proponer el cambio en `~/vortex-api` en vez de un parche local.
