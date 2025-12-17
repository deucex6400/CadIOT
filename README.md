
# CAD Dispatch – Azure Functions + ESP32 (v4, App Configuration everywhere)

This package includes:
- Azure Functions (.NET 8, isolated) with **App Configuration** for all runtime settings.
- IoT Hub Direct Method + C2D fallback.
- Microsoft Graph webhook + subscription timer.
- Azure Table Storage audit logging.
- HTTP test harness.
- ESP32 sketch.
- RBAC for Applications scripts.
- A full PDF in `/docs` with step-by-step Azure Portal setup.

## Quick start
1. Configure Azure App Configuration (or `local.settings.json`) with keys listed in the PDF.
2. Run locally: `func start`.
3. Test: `GET http://localhost:7071/api/TestRelay?relay=1`.
4. Deploy to Azure: `az functionapp deployment source config-zip --src cad_dispatch_v4.zip`.
