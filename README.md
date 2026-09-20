# Bannerlord AI Control

Strategic-control bridge for **Mount & Blade II: Bannerlord v1.4.8.119303**.

The bridge exposes structured campaign telemetry and semantic campaign commands so an AI can make strategic decisions without brittle screen-coordinate automation. Real-time combat automation is intentionally out of scope; battles use Bannerlord's native **Send Troops / battle simulation** path.

## Local development

- `SubModule.cs` — primary bridge source
- `compile_mono20.rsp` — Bannerlord Mono/.NET Standard 2.0 build response file
- `blctl.py` — atomic command/response client
- `bl-dev.ps1` — build, deploy, logs, sync, command, push helper

Typical commands:

```powershell
.\bl-dev.ps1 status
.\bl-dev.ps1 build
.\bl-dev.ps1 command status
.\bl-dev.ps1 logs
.\bl-dev.ps1 sync
.\bl-dev.ps1 push "describe the change"
```

Deployment refuses to overwrite the bridge DLL while Bannerlord is running.

See `docs/AGENT_HANDOFF.md` for the parallel-agent contribution model.
