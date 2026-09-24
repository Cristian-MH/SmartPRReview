# AI and runner configuration

The application ships with model candidates configured and server credentials and
execution disabled. No secrets are included. OpenAI and Gemini models with native
token counting appear in Vue without server keys. Enter the selected provider's
API key in Vue: it is sent as `aiApiKey` in the POST body for that review only.
It takes precedence over an optional server credential. An invalid request key
does not trigger fallback to the server key or another provider.

Keys remain in browser memory, never local/session storage, and are cleared on
provider change or page reload. Backend credential state is scoped to the HTTP
request and is excluded from prompts, cached reviews and progress/results. Use
HTTPS outside localhost and keep request-body logging disabled at reverse proxies.

Only server-allowlisted model IDs can be selected. Native-counting candidates can
be tried with a request key before integration verification; their report includes
a limitation that prevents an Approve recommendation. Models using a conservative
token bound still require independent verification before they appear in the catalog.

Example for both `/api/reviews` and `/api/reviews/stream`:

```json
{
  "provider": "GitHub",
  "location": "https://github.com/UCR-Labs/Coope-Web",
  "pullRequestNumber": 32,
  "aiApiKey": "YOUR_PROVIDER_KEY",
  "ai": {
    "provider": "OpenAI",
    "classificationModel": "gpt-5-mini",
    "reviewModel": "gpt-4.1"
  }
}
```

## Optional server credentials and integration verification

This setup is optional when using a key from Vue. To configure shared server
credentials, set the key in the terminal used for the API:

```powershell
$env:AI__Providers__OpenAI__ApiKey = "YOUR_KEY"
dotnet run --project backend/SmartPRReview.Api -- --verify-ai OpenAI
dotnet run --project backend/SmartPRReview.Api
```

The verification command explicitly starts up to three billable model calls with
synthetic data, never repository code. It checks classification JSON, a tool call,
the final review JSON, native token counting, reported usage and configured token
limits through the real adapters. No retries or fallback occur. On success it
writes the selected models as verified and enables the provider in the ignored
`backend/SmartPRReview.Api/appsettings.Ai.local.json`. Environment keys are not
copied into that file. Restart the API and reload Vue after verification.
The key must also be available to the process that starts the API (including
your IDE if you launch it there). This setup check is not a quality benchmark.

| Provider | Classifier | Reviewer |
| --- | --- | --- |
| OpenAI | `gpt-5-mini` | `gpt-4.1` |
| Gemini | `gemini-2.5-flash` | `gemini-3-flash-preview` (preview) |
| DeepSeek | `deepseek-flash` | `deepseek-v4-pro` |

For Gemini set `AI__Providers__Gemini__ApiKey` and use `--verify-ai Gemini`.
DeepSeek remains disabled until its conservative token bound is independently
evaluated; the setup command deliberately cannot certify that bound from one
sample. Models must be accessible to your account. A failed check leaves the
configuration unchanged and returns a nonzero exit code.

For custom settings, copy `ai.example.json` to `backend/SmartPRReview.Api/appsettings.Ai.local.json`
only if that file does not already exist (do not overwrite a verified setup).
For each provider, configure actual account-accessible model IDs, context limits,
role capabilities and defaults. Run the opt-in contract/evaluation checks before
setting `Verified: true` and `Enabled: true`. The classifier needs structured
output; reviewers also need tool calls. Configure only tested combinations.

Set credentials on the API process:

```powershell
$env:AI__Providers__OpenAI__ApiKey = "YOUR_KEY"
$env:AI__Providers__Gemini__ApiKey = "YOUR_KEY"
$env:AI__Providers__DeepSeek__ApiKey = "YOUR_KEY"
dotnet run --project backend/SmartPRReview.Api
```

`Native` token counting uses provider endpoints (OpenAI/Gemini). DeepSeek requires
`VerifiedUtf8UpperBound`: the entire serialized wire request's UTF-8 byte length
plus 4096 framing tokens is reserved. This is deliberately conservative and must
be verified against usage for the chosen text-only model before enabling it.
Models without a validated counting method stay disabled. A reported overrun
fails the stage; it cannot retroactively undo provider billing. TOON selection
uses native counts only; upper-bound mode uses compact JSON.

Model rates are optional USD per million tokens: `InputRate`, `CachedInputRate`,
`OutputRate`. Missing usage or rates appears as N/D, never zero. The app does not
automatically change providers or choose a different model after a failure.

## Dedicated runner

Run this service only on a dedicated host authorized to execute code from the
configured repositories. Use Docker Desktop's Linux engine for local development.
The API never needs Docker access. The runner has Docker access; PR containers do
not. Protect the runner's network endpoint with TLS outside localhost.

Copy `runner.example.json` to
`runner/SmartPRReview.Runner/appsettings.Runner.local.json`. Replace repository,
image digest, working-directory and command placeholders. Image digests must be
valid SHA-256 values; pre-pull the verified images on the runner. Node repositories
must define an actual non-watch unit-test script; the SmartPRReview frontend's
`npm test` is browser E2E and must not be used as a unit-test profile.

Create an **internal** Docker network named `smartpr-restore`. Connect a trusted
registry proxy to it and to an egress network. Permit only approved registry
hosts (for example registry.npmjs.org and the NuGet feed/CDN domains required by
your profiles), deny all other destinations, and expose no credentials. Configure
the proxy's internal address in `RegistryProxy`. The runner verifies the restore
network is internal; operators must ensure no other gateway/privileged peer is
attached. PR containers join this network only for restoration, then disconnect
before build/test. There are no host workspace mounts.

```powershell
# Runner terminal; configure a long random shared value through your secret store.
$env:Runner__ApiKey = "YOUR_RUNNER_SHARED_SECRET"
dotnet run --project runner/SmartPRReview.Runner --urls http://localhost:62700

# API terminal: use the same shared value.
$env:ExecutionRunner__ApiKey = "YOUR_RUNNER_SHARED_SECRET"
dotnet run --project backend/SmartPRReview.Api
```

Both base and head repositories (including forks) must be allowlisted. Profiles
are server-owned; source code and AI responses cannot supply runner commands.
Multiple profiles for one repository execute sequentially. A busy runner rejects
new requests immediately. Work is capped at 2 CPUs, 4 GiB RAM, 256 processes and
10 minutes, with 1 GiB tmpfs for source/dependencies and 1 GiB for temporary files.
Restoration failure is Unavailable; build/unit-test nonzero exit is Failed and
includes bounded evidence. Failures are not claimed to be regressions without a
baseline run. An administrator must distinguish external runtime/configuration
requirements from code defects when interpreting logs.

The runner never receives GitHub/OpenAI keys. The API downloads the exact head
archive and sends it to the authenticated runner. Zip extraction rejects escaping
paths, symlinks, oversized archives and duplicate files. Containers and temporary
workspaces are removed after success, failure, timeout or client cancellation.

Production deployments should terminate TLS and disable reverse-proxy buffering
for `/api/reviews/stream`, with a timeout exceeding runner and model stage limits.
There is still no application-level user authentication or durable database.
Do not expose private review results in an unauthenticated multi-user deployment.
