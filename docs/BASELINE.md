# Current baseline

## Imported runtime

This repository preserves the current local integrated-preview source under `src/current/`.

Observed live version at bootstrap: `1.3.6-integrated-preview.1`.

Imported core files:
- `PCBridgePortable.cs`
- `ScopedBridge.cs`
- `DesktopIntegration.cs`
- `IntegrationTests.cs`
- `ScopeTests.cs`
- Desktop Commander launcher/package metadata where safe to import

The current implementation already includes:
- local DPAPI-protected state
- tunnel/MCP bootstrap
- scoped legacy task permissions
- Desktop Commander integration
- non-HARDLOCK automatic execution
- HARDLOCK classification
- local approval UI
- task leases
- integration tests
- experimental one-shot chat relay

## Known baseline defects

The v1.3.6 relay has a foreground/focus-based immediate SendKeys path. It can deliver to the wrong ChatGPT conversation when the user is typing elsewhere. It is retained only as historical/current-state source, not as the target architecture.

The existing codebase also contains duplicated/experimental build scripts and generated artifacts in the local working directory. Those are intentionally not imported wholesale.

## Excluded from Git

- API keys, tunnel credentials and authentication/browser state
- DPAPI blobs and local runtime state
- generated EXEs, ZIPs and backups
- node_modules and embedded runtime binaries
- logs, screenshots and test artifacts
- one-off patch/install archaeology unless later promoted into a clean reproducible build pipeline

## Repository rule

`src/current/` is the baseline to preserve behavior while vNext is designed. New architecture work should not silently rewrite the baseline without a migration plan and regression evidence.
