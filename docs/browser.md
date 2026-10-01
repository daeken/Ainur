# Browser and computer-use tools

Ainur agents can drive a headless browser to complete web tasks. The browser is an isolated Chrome/Chromium process with a disposable profile: it does **not** attach to the user's browser, cookies, or extensions. Built-in browser tools are registered but **optional** — discover them with `find_tools` and load only the ones needed with `load_tools`. Computer use is a screenshot → one action → screenshot loop exposed as a normal function tool, so it works with any model; the ChatGPT subscription Responses endpoint does not accept a native `computer` tool.

## Requirements

- Google Chrome or Chromium installed on the host. You can override the binary with `AINUR_BROWSER_EXECUTABLE=/path/to/chrome`.
- Browser sessions launch with `--headless=new` by default. The browser subprocess is bound to 127.0.0.1 via an ephemeral Chrome DevTools Protocol port; the profile lives under `<Ainur home>/browser/<session id>/profile` and is deleted on close.
- A browser session is created lazily on the first browser tool call and owned by its Ainur session id. Idle sessions are reaped after 10 minutes. One page is supported per session.

## Agent tool surface

| Tool | Purpose |
| --- | --- |
| `browser_navigate` | Navigate to a URL and return an observation plus a screenshot artifact. |
| `browser_read` | Read visible text, accessibility tree, links, or indexed interactive elements without a screenshot. |
| `browser_click` | Click by CSS selector, element index from `browser_read`, or viewport CSS pixel coordinates. |
| `browser_type` | Type into the focused element or focus by CSS selector/element index first. |
| `browser_screenshot` | Capture the viewport as a PNG artifact. |
| `browser_wait` | Wait for time to elapse or for a selector to appear. |
| `browser_back` | Go back in history. |
| `computer` | Perform exactly one action (`screenshot`, `click`, `type`, `key`, `scroll`, `navigate`, `wait`); return an observation and screenshot. |

Tool responses include the current URL, page title, visible text or indexed elements, and an artifact reference such as `sha256:…` for the PNG. Screenshots are stored in the same content-addressed artifact store as other tool results and recorded in the normal tool invocation history. A `browser_read` call is cheaper than sending a screenshot whenever text or the accessibility tree suffices.

**Recommended pattern:** `browser_navigate` → `browser_read` with `mode=interactive` → `browser_click` using the returned element index → `browser_read` or `browser_screenshot` to verify. Use `computer` for visual or coordinate-driven interactions where semantic elements are insufficient. Indices are scoped to the page state that produced them; read again after navigation.

## Live view

The web UI can watch a browser session over the browser API, implemented in `src/Ainur.Server/BrowserApi.cs` and backed by `BrowserManager`:

```text
GET /api/v1/browser/sessions                 # index of live sessions (JSON array of session snapshots)
GET /api/v1/browser/sessions/{id}/frame      # one-shot latest frame (SSE event; 204 before first capture)
GET /api/v1/browser/sessions/{id}/stream     # SSE live feed; 404 when session unknown
```

A new stream replays the `session` snapshot and latest `frame` (if any), then sends contract-shaped `frame` events as screenshots are captured. Frames are produced after tool actions and by a background pump while at least one subscriber watches. An SSE `closed` event is terminal and ends the HTTP response. The server sends `:ka` comments every 15 seconds while connected. `reference/browser-stream-contract` in project knowledge is the authoritative wire contract (event names, JSON fields, ordering, `EventSource` error-name collision). The UI's base path defaults to `/api/v1/browser/sessions` and can be set by `VITE_BROWSER_STREAM` or `?browserStream=<base>` for a disposable local server.

## Safety and limits

- Do not give the browser untrusted local file paths or credentials. The browser has normal local-process privileges; this is profile isolation, **not a security sandbox**. The CDP debug port is loopback-only, not remotely accessible.
- The toolset does not handle file upload/download, browser dialogs, or multiple tabs yet.
- The API is read-only; browser actions are available only through agent tool calls.
- Browser tools are registered but not loaded into every agent by default. The `computer` tool does not use a provider-native computer-use API.

## Local verification

```sh
dotnet test tests/Ainur.Tests/Ainur.Tests.csproj --filter 'Category=Browser'
dotnet test --filter 'Category!=Live'
```

`BrowserTests.cs` drives real headless Chrome against a local HTTP fixture, with timeouts and process cleanup. It checks navigate/screenshot/click/back/type, that a 40ms frame pump does not starve five concurrent action round trips, and the stream payload/termination contract. A disposable API-integration harness that exercises index, frame, and SSE routes end-to-end lives outside the repo at `/tmp/ainur-routes` on the development host; its raw output is `/tmp/ainur-routes/run3.log` (not shipped). Design rationale and capability-probe evidence are in project knowledge `design/browser-computer-use` and `reference/browser-stream-contract`.
