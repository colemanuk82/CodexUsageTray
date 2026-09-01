# Codex Usage Tray

A lightweight Windows system-tray monitor for Codex limits, usage history, model activity, and estimated API cost.

## Features

- Five-hour and weekly remaining-usage meters.
- Tray icon with separate five-hour and weekly usage bars.
- Live reset countdowns for both limit windows.
- Weekly usage history graph with 1-day, 7-day, and 30-day ranges.
- Hourly points and time markers in the 1-day graph.
- Historical reset markers and reset-probability information.
- Codex usage panel with total tokens, calls, sessions, cached input tokens, and average daily usage.
- Model usage breakdown with proportional usage bar, token totals, and percentages.
- Predicted API cost by model and estimated total cost.
- Automatic refresh at a selectable 1-minute or 5-minute interval.
- Popout switcher between Codex limits and Codex usage.
- Start with Windows option.
- Local history and rate caching.
- No account credentials or usage history are uploaded by this application.

## Integrations

### Codex usage service

The app reads the live Codex usage response from the ChatGPT backend endpoint:

- `https://chatgpt.com/backend-api/wham/usage`

It uses the existing Codex authentication token from the local file below and does not ask the user to enter credentials:

- `%USERPROFILE%\\.codex\\auth.json`

### Codex CLI session history

Model usage, token totals, calls, sessions, and cost estimates are calculated from local Codex CLI session records:

- `%USERPROFILE%\\.codex\\sessions`

The app reads `token_count` records and keeps its local snapshot history under:

- `%LOCALAPPDATA%\\CodexUsageTray\\state.json`

### Codex Reset Today API

Historical reset data and reset estimates come from the public, read-only Codex Reset Today REST API. No API key is required.

- [API and MCP documentation](https://codex-reset.today/developers)
- [Current reset status API](https://codex-reset.today/api/v1/status)
- [Historical reset announcements API](https://codex-reset.today/api/v1/resets?limit=100&order=desc)
- [Codex Reset Today](https://codex-reset.today/)

The app uses the status endpoint for the next-reset probability and the resets endpoint for timestamped regular and banked reset history. API results are cached locally for use between refreshes.

### Model pricing

The estimated-cost view uses built-in fallback rates and periodically checks public model pricing pages when a matching model is available. Rates are cached locally for 24 hours. Estimates are informational only and are not billing data.

## Privacy

All usage processing and history storage are local to the Windows machine. The app contacts only the Codex usage endpoint, the Codex Reset Today API, and public model-pricing pages used for cost estimates. It does not collect telemetry or transmit local session history.

## Requirements

- Windows 10 or later.
- .NET 8 desktop runtime when using the framework-dependent build.
- An existing Codex CLI installation and authenticated Codex session.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

For a self-contained single-file executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The published executable is located at:

```text
bin\\Release\\net8.0-windows\\win-x64\\publish\\CodexUsageTray.exe
```

## Disclaimer

This is an independent utility and is not affiliated with or endorsed by OpenAI. The usage meter shown by the Codex account remains authoritative. Reset forecasts and API cost figures are estimates and should not be treated as official billing or quota information.
