# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation
Rama: `feat/CER-M0-001-bootstrap`
Estado: CER-M0-001 completada; no hay funciones de producto implementadas.

## Listo

- documentación de producto, arquitectura, seguridad y flujo de trabajo;
- inicio limpio, sin reutilizar la implementación de cuotas de WidgetOSD;
- solución con cuatro proyectos de producto y tres proyectos de pruebas;
- referencias entre proyectos conforme a `docs/ARCHITECTURE.md`;
- SDK .NET 10.0.401 disponible; Graphify 0.9.57 ya estaba instalado y el grafo se actualizó.

## Verificación

- `dotnet build Cerith.sln`: exit 0; 0 advertencias y 0 errores.
- `dotnet test Cerith.sln`: exit 1; MTP devuelve `ZeroTests` (8), con 0 pruebas descubiertas o ejecutadas. No hubo pruebas fallidas.
- El comando de pruebas de CI termina con exit 0 solo porque la excepción temporal `--ignore-exit-code 8` tolera `ZeroTests` durante el esqueleto; ver D-012.

## Bloqueado

- ninguno.

## Siguiente

CER-M0-002 sigue `TODO` y `unclaimed`; no se ha iniciado.
