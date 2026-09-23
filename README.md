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

## Party and economy commands

The bridge now supports `troops`, `recruits` (`inspect_recruits`), `inventory`,
`market`, `economy_status`, and guarded single-volunteer recruitment with
`recruit_one`. Market inspection includes modified item stacks and current unit
buy/sell quotes for both the market and player inventory.

See [the command contract and disposable-save test plan](docs/PARTY_ECONOMY.md).
These additions compile against the installed game assemblies and have offline
regression coverage; live-save validation is still pending. Buying/selling,
bulk recruitment, upgrades, and prisoner mutations are not exposed by this patch.

```powershell
.\bl-dev.ps1 build
.\tests\Test-PartyEconomy.ps1
```
