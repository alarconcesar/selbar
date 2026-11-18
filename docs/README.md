# Documentación de Selbar

El [README](../README.md) es la guía de usuario. Esta carpeta es para desarrollo.

## Lectura recomendada

| Documento | Para qué |
|---|---|
| [AGENTS.md](../AGENTS.md) | Reglas duras, comandos y checklist para agentes. |
| [ARQUITECTURA.md](ARQUITECTURA.md) | Capas, flujos, threading, ventanas, tema, IA y tests. |
| [COMO_EXTENDER.md](COMO_EXTENDER.md) | Recetas: acciones, ventanas, ajustes, colores, integraciones. |
| [DECISIONES.md](DECISIONES.md) | Por qué el indicador, por qué no Mica, DPAPI, offline, etc. |
| `screenshots/` | Capturas usadas por el README; se generan con `SelfTest --shots`. |

## Atajo

```powershell
dotnet build tools\SelfTest\SelfTest.csproj -c Release
dotnet run --project tools\SelfTest -c Release --no-build
dotnet run --project tools\SelfTest -c Release --no-build -- --ui
.\build.ps1
```

Si sólo vas a cambiar una acción, empieza por
[COMO_EXTENDER.md](COMO_EXTENDER.md#añadir-una-acción).
