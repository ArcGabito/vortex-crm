# Vortex CRM

Gestión comercial de la suite **Vortex** (**ERP, CRM, WMS, POS**) para negocios peruanos: libre y de código abierto
(AGPL-3.0), hecho en Perú para LATAM y el mundo. Es una app **.NET MAUI Blazor Hybrid** para Windows, Android, iOS y
macOS que funciona sin conexión y se integra con el resto de la suite a través del hub
[vortex-api](https://github.com/ArcGabito/vortex-api).

## Qué hace

- **Clientes** (empresas con RUC o personas con DNI) compartidos con toda la suite, y sus **contactos**.
- **Ficha del cliente** de 360°: oportunidades, cotizaciones, tareas y la actividad que informan los otros sistemas
  (compras en caja del POS, comprobantes y pagos del ERP, despachos del WMS).
- **Pipeline** por etapas (prospecto, cotizado, negociación, ganado, perdido) con montos, cierre estimado y tasa de cierre.
- **Cotizaciones** en soles con IGV incluido, adicional o sin IGV (Nuevo RUS), desde el catálogo o con líneas libres;
  **PDF** con el monto en letras y envío por **WhatsApp**. Al aceptarse, la oportunidad se gana sola y el ERP la ve
  como cotización por convertir; también se puede generar el pedido desde aquí.
- **Tareas** (llamadas, WhatsApp, reuniones, visitas) con responsable y **avisos** en Android.
- Varios vendedores y equipos de la misma empresa comparten todo, aun trabajando sin conexión.

## Cómo encaja en la suite

| Con | El CRM… |
|---|---|
| Hub | Inicia sesión (sin usuarios propios) y sincroniza clientes, productos, sedes y datos de la empresa. |
| ERP | Publica `quote.accepted`, `deal.won` y `order.created`; recibe `invoice.issued` y `payment.received`. |
| WMS | Recibe `shipment.dispatched` (el pedido salió). |
| POS | Recibe `sale.completed` de los clientes identificados en caja. |

Ninguno es obligatorio: sin ERP el CRM igual cotiza y gana oportunidades. El detalle está en
[docs/INTEGRACION.md](docs/INTEGRACION.md).

## Empezar

Requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download) con el workload de MAUI
(`dotnet workload install maui`) y el hub corriendo en local (Docker).

```bash
git clone https://github.com/ArcGabito/vortex-api ~/vortex-api
cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http   # http://localhost:5299

git clone https://github.com/ArcGabito/vortex-crm && cd vortex-crm
scripts/pack-sdk.sh                                                  # SDK del hub -> packages/vortex-sdk
dotnet test tests/VortexCrm.Core.Tests                               # pruebas (no necesitan el hub)
dotnet run --project src/VortexCrm -f net10.0-windows10.0.19041.0    # app en Windows (también -f net10.0-android)
```

En Windows con el hub dentro de WSL, empaqueta el SDK con `./scripts/pack-sdk.ps1`. Usuario demo:
`luis@vortex.local` / `Suite-Local-2026!` (en Debug, **Usar cuenta demo** en el login). La clave de sistema de
desarrollo es `vxp_crm_dev_key_change_me` y se cambia desde el login (**Servidor → Cambiar**).

## Estructura

```
src/
├── VortexCrm.Core/    Dominio (cotizaciones, oportunidades, tareas), servicios, PDF, sincronización y eventos (sin MAUI)
└── VortexCrm/         App MAUI Blazor Hybrid: pantallas, sesión con el hub, avisos de Android y sistema de diseño
tests/
└── VortexCrm.Core.Tests/
```

## Contribuir

Lee [CONTRIBUTING.md](CONTRIBUTING.md). `main` está protegida: los cambios entran por pull request con el CI en verde.
Para vulnerabilidades, [SECURITY.md](SECURITY.md).

## Licencia

[GNU AGPL-3.0](LICENSE.md). Si ofreces Vortex CRM como servicio en red, debes publicar tus cambios.
