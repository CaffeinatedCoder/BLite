# BLite.Wasm browser tests

Runs BLite.Wasm in a real browser. Nothing else in `tests/` exercises the OPFS or
IndexedDB backends, the single-threaded browser runtime, or a Release (trimmed)
publish.

```bash
dotnet test tests/BLite.Wasm.BrowserTests
```

The fixture:

1. publishes `tests/BLite.Wasm.BrowserHost` (a Blazor WebAssembly app that exposes
   each scenario as a `[JSInvokable]` method),
2. serves the publish folder from Kestrel on `http://localhost:<port>` (a secure
   context, which OPFS needs),
3. installs the Playwright browser on first use and opens a fresh browser context
   per test, so every test starts with empty OPFS and IndexedDB storage.

A scenario ends in one of four states, all appended to a JSON-lines report:

| Status  | Meaning |
|---------|---------|
| `pass`  | The scenario returned and its expectations held. |
| `fail`  | The scenario threw; the report has the exception type, message and stack. |
| `hang`  | No answer before the timeout: a blocking wait froze the browser's only thread. |
| `crash` | The .NET runtime in the tab exited (a failed runtime assertion). |

## Variables

| Variable | Default | Values |
|----------|---------|--------|
| `BLITE_WASM_VARIANT` | `trimmed` | `trimmed` (Release publish, the Blazor default `TrimMode=partial`), `fulltrim` (`TrimMode=full`), `untrimmed` (`PublishTrimmed=false`), `aot` (`RunAOTCompilation=true`, needs `dotnet workload install wasm-tools`) |
| `BLITE_WASM_BROWSER` | `chromium` | `chromium`, `firefox`, `webkit` |
| `BLITE_WASM_PUBLISH_DIR` | – | Serve an existing publish folder instead of publishing. |
| `BLITE_WASM_REPORT` | `bin/.../TestResults/wasm-browser-<browser>-<variant>.jsonl` | Report file. |

Each variant is published with its own `--artifacts-path`, because an incremental
publish would otherwise reuse the trimmed assemblies of the previous variant. The
publish log (including any trimming or AOT warnings) is written next to the test
assembly as `publish-<variant>.log`.
