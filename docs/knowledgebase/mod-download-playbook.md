# Mod-Build Download Playbook (Living Doc)

Practical, per-host procedures for automating the "click through and download" step of
installing a `mod-builds` TOML/Markdown build (e.g. `KOTOR1_Full.toml`) without a human
at the keyboard. This doc is meant to be appended to whenever a new quirk is discovered —
add a dated note under the relevant host section rather than rewriting it.

Tooling used: `claude-in-chrome` (Chrome browser automation, available as
`mcp__claude-in-chrome__*` tools) for anything that needs a real browser session;
FlareSolverr (`http://localhost:8191`) as a fallback cookie/session solver for
Cloudflare-gated hosts when a full browser session is overkill; plain `curl`/`dotnet`
for everything else. No ad hoc `.sh` files — every action is an inline command or an
inline browser-automation call.

**2026-07-30: `claude-in-chrome` was confirmed unavailable in this environment** (the
Chrome extension reports "not connected" consistently — checked from both the parent
session and this subagent, not a one-off). **Use `agent-browser` instead** — a real CLI
browser automation tool already installed (`~/.cargo/bin/agent-browser`, Chrome-backed,
confirmed working). Key commands: `agent-browser open <url>`, `click <sel>`,
`download <sel> <path>`, `get title/url/text`, `screenshot <path>`, `snapshot -i`
(accessibility tree of interactive elements — useful for finding what to click without
a screenshot), `wait --load networkidle`. Commands chain with `&&` and the browser
persists across separate CLI invocations (daemon-backed) — no per-call reconnect
needed. Fully inline, no script files. Run `agent-browser --help` for the complete
command reference.

**HARD RULE: never attempt to solve, click through, or otherwise bypass a CAPTCHA or
"verify you are human" challenge** (Cloudflare Turnstile checkbox, reCAPTCHA, hCaptcha,
etc.), regardless of how automatable it looks with a real browser tool. This is a firm
safety boundary, not a per-run judgment call. If a page under automation shows a visible
CAPTCHA/Turnstile checkbox, **stop on that specific mod immediately** and log it as
**blocked-pending-user-action** (distinct from a dead link or a premium-only file) —
the user themselves would need to solve it in their own browser, or the mod build owner
would need a Nexus API key to bypass the browser flow entirely via the official API. Do
not retry the same CAPTCHA-gated URL hoping it resolves on its own the way DeadlyStream's
JS-timing "One moment, please..." challenge does — a Turnstile checkbox is a genuine
human-verification gate, not a timing challenge.

## FlareSolverr

- The real API is `POST http://localhost:8191/v1` with a JSON body, e.g.
  `{"cmd":"sessions.list"}` or `{"cmd":"request.get","url":"...","maxTimeout":60000}`.
  **A bare `GET /` returns 404 and is not a health check** — don't mistake that for
  FlareSolverr being down.
- `sessions.list` / `sessions.create` / `sessions.destroy` manage a persistent solved
  session so repeated requests to the same Cloudflare-gated host don't re-solve the
  challenge every time.
- Confirmed running as of 2026-07-30, version `1.1.0-patchright` (it uses Patchright
  under the hood, not vanilla Playwright — Patchright patches known headless-detection
  fingerprints, which matters for hosts that fingerprint the browser rather than just
  checking a JS challenge).

## deadlystream.com (~180 of 189 mods — the bulk of the build)

- Plain file/category pages return HTTP 200 to a bare `curl` — no blanket Cloudflare
  wall on browsing. This means the prior run's near-total failure was **not** a bot-wall
  problem across the board.
- Actual attachment/download endpoints (typically
  `.../applications/core/interface/file/attachment.php?id=<n>` or a "Download this
  file" button on the file page) may require an active forum session, a Referer header,
  or simply have stale/renumbered ids in the TOML if the build is older than the current
  site structure — treat each failure as "investigate this specific link," not "the host
  is blocked."
- **Recommended flow:** navigate to the file page with `claude-in-chrome`, find the
  actual download control (button/link text varies — "Download this file", a version
  entry, etc.), click it, and capture the resulting download. Only reach for
  FlareSolverr-solved cookies + `curl` if the click-through proves unreliable for a
  given file (e.g. Chrome tools time out repeatedly on the same URL).
- **2026-07-30 (this run): the real failure mode found.** ModSync's own
  `DeadlyStreamDownloadHandler` resolves ALL 168 DeadlyStream URLs in the build
  essentially simultaneously when `--concurrent` is passed (or in a tight sequential
  loop with no delay otherwise). Within the first ~30 seconds of hitting the site this
  hard, DeadlyStream's edge (`159.89.148.93`) started serving a JS anti-bot challenge
  page (`<title>One moment, please...</title>`, "Please wait while your request is
  being verified...", an obfuscated fingerprint-check script) to the handler's
  `HttpClient` — which cannot execute JS, so every single resolution failed with
  "Could not extract csrfKey from page" / "No file attachments/download links found".
  Within another ~2 minutes, the block escalated further: **the site became fully
  unreachable at the TCP level** (`curl` connect times out after 15s with no response
  at all, `HTTP 000`) — not just from this tool, but from a completely separate `curl`
  process AND from FlareSolverr's own Patchright-driven browser (`page.goto: Timeout
  30000ms exceeded`). This means it's a network/WAF-level block on this environment's
  egress IP, not an application-layer bot-check, and it persisted for at least 20+
  minutes after the burst (still blocked when re-tested at 11:33, ~18 min after the
  process that caused it was killed at 11:21). **Do not re-run the installer's
  `--concurrent` flag against DeadlyStream URLs again until connectivity is confirmed
  recovered** (`curl -m 10 https://deadlystream.com/` returning something other than
  `000`/timeout) — retrigger it and the block window likely resets. When it does
  recover, use the installer WITHOUT `--concurrent` (or better, patch in a
  several-hundred-ms delay between DeadlyStream requests) so it behaves like a human
  browsing session instead of a scraper burst.
- The prior run's "near-total failure" (the one this playbook's original write-up
  described as "likely dead/renumbered attachment ids") was actually this same
  self-inflicted rate-limit/block, not stale ids — the site structure looks intact
  from the one debug HTML captured (a real IPS/Invision forum file page), it's just
  that literally none of the requests got through before/during the block.
- **2026-07-30 (root cause identified): Cloudflare WARP was the real culprit, not just
  our request burst.** This machine runs `warp-cli` in full-tunnel `Mode: Warp` with
  `Always On: true` — all outbound traffic (including the DeadlyStream requests above)
  was egressing through Cloudflare's shared consumer WARP exit IP. That shared IP has
  presumably accumulated a bad reputation from other WARP users' traffic, so
  DeadlyStream's own protection (host-level firewall/WAF, not necessarily Cloudflare's
  edge — the site's `curl -I` response shows plain `Server: Apache` with no `cf-ray`
  header, so it may not even be Cloudflare-fronted) blocks that IP outright, and our
  concurrent burst was enough to trip it. **Fix that actually restored access:**
  ```
  warp-cli tunnel host add deadlystream.com
  warp-cli tunnel host add www.deadlystream.com
  ```
  This adds a split-tunnel exclusion so traffic to those two hostnames bypasses the WARP
  tunnel entirely and goes out over the normal ISP connection, while WARP stays on
  (`Always On: true`, other traffic unaffected) for everything else. Verified working
  immediately after (`curl -m 10 https://deadlystream.com/` → `200`, ~1s response, no
  further wait needed) — this was a WARP split-tunnel config issue, not a time-based
  block that needed to "cool down." **If DeadlyStream (or any other host) starts
  timing out at the TCP level again, check `warp-cli tunnel host list` first** and add
  the host if it's missing, before assuming a fresh bot-block or waiting it out.
  Still worth adding a small delay between DeadlyStream requests going forward (see
  above) since the underlying site may have its own separate rate-limiting on top of
  the now-resolved WARP IP-reputation issue.
- Check `warp-cli tunnel host list` for the current exclusion list; `warp-cli status`
  / `warp-cli settings` show whether WARP is even active in the current environment —
  this may not apply on environments where WARP isn't installed/running.

## nexusmods.com (23 mods)

- Bare `curl` gets HTTP 403 (Cloudflare/bot-check) — needs a real browser context.
- No `NEXUS_API_KEY` present in this environment, so the API-based direct-download
  endpoint is not available; every Nexus mod goes through the free "Slow download"
  button flow: open the mod's Files page, pick the right file/version, click
  "Slow download," and the site holds a countdown timer (historically ~5s-ish, subject
  to change) before revealing the actual download link/triggering the download.
- Some files are premium-only with no free download path at all — if a file page shows
  no free download option, log it in the progress doc as unobtainable-without-payment
  rather than retrying indefinitely.
- **2026-07-30: full working recipe found — Nexus IS obtainable without premium, without
  API key, and without solving any CAPTCHA.** Screenshots for every step below are
  saved in `docs/knowledgebase/nexus-flow-screenshots/` (numbered 01-09) — refer to them
  directly rather than re-deriving selectors from scratch.

  **Why earlier attempts failed:**
  - `claude-in-chrome` was confirmed disconnected all session (extension never
    connected) — irrelevant now that `agent-browser`/Patchright work, but don't waste
    time retrying it first.
  - The Nexus API's `download_link.json` endpoint (used automatically by
    `NexusModsDownloadHandler.DownloadWithApiKey`) returns **HTTP 403 "You don't have
    permission to get download links from the API without visiting nexusmods.com -
    this is for premium users only"** for any non-premium account, even with a valid,
    validated API key (confirmed directly against the real endpoint,
    `is_premium:false` account). **An API key alone does not unlock free-tier Nexus
    downloads via the CLI's automatic path — only Premium accounts can skip the
    browser.** Free/Supporter-tier accounts must go through the manual browser flow
    below every time.
  - `agent-browser`'s default engine is literally Chrome-for-Testing running
    **headless** — Cloudflare serves an actual interactive Turnstile "verify you are
    human" checkbox to it (see `01-headless-turnstile-captcha-blocked.png`), which is a
    hard stop (never solve/click through a CAPTCHA — see the hard rule above).
  - Switching to **Patchright, headed** (not headless), with a **persistent
    user-data-dir**, made the Turnstile disappear entirely on the mod page — real
    headed Patchright reads as a normal browser to Cloudflare (`02-...png`). The
    remaining wall at that point was Nexus's own **login requirement** ("You have to be
    logged in to download files") — solved by the user logging in once, by hand, in
    that visible window (never enter a password yourself — see the password rule under
    Prohibited actions). The login persists in the profile directory afterward.
  - The Nexus **login page itself** also showed a Cloudflare Turnstile "Verification
    failed" widget under **headless** Chrome-for-Testing (same root cause) — another
    data point that headless is what triggers Cloudflare here, not something specific
    to one page.

  **Launching a controllable, persistent, headed Patchright session** (do this once,
  keep it running, reconnect via CDP for every subsequent action instead of relaunching
  — relaunching each time is slow, pops a new window, and loses any in-page state):
  ```python
  # one-time launch, backgrounded (nohup ... & disown), NOT closed afterward:
  from patchright.sync_api import sync_playwright
  import time
  with sync_playwright() as p:
      ctx = p.chromium.launch_persistent_context(
          user_data_dir='/home/<user>/.patchright-nexus-profile',  # persists login
          headless=False,
          viewport={'width': 1280, 'height': 900},  # advisory only — see gotcha below
          accept_downloads=True,
          args=['--remote-debugging-port=9333'],
      )
      page = ctx.pages[0] if ctx.pages else ctx.new_page()
      page.goto('https://www.nexusmods.com/kotor/mods/<id>?tab=files')
      time.sleep(3600)  # keep the context alive; do real work via CDP reconnects below
  ```
  Then, in every subsequent script:
  ```python
  from patchright.sync_api import sync_playwright
  with sync_playwright() as p:
      browser = p.chromium.connect_over_cdp("http://localhost:9333")
      page = browser.contexts[0].pages[0]
      # ... act on `page` ...
      # DO NOT call browser.close() — see gotcha below
  ```

  **Gotchas that cost real time — read before automating this again:**
  1. **`browser.close()` after `connect_over_cdp()` sends a real CDP `Browser.close`
     and kills the actual browser process**, not just the client connection. It is not
     a "disconnect." Never call it after reconnecting via CDP — just let the script
     end and the WebSocket drop on its own.
  2. **The requested `viewport` size is not honored when reconnecting via CDP to an
     already-launched persistent context** — the real screenshot/coordinate space ends
     up being whatever the actual OS window size is (observed: requested 1280×900, got
     913×931). Always read the actual PNG dimensions
     (`struct.unpack('>II', open(path,'rb').read(33)[16:24])`) before trusting pixel
     coordinates from a screenshot — don't assume the requested viewport size.
  3. **The "Manual download" confirmation modal's real button lives in what appears to
     be a closed shadow root** (`<div id="next-shadow-root">` intercepts pointer
     events; `get_by_role`/`get_by_text` cannot find it at all — a locator scoped to
     "Manual download" only ever matches the *background* file-card buttons, never the
     modal's, and clicking those does nothing useful because they're covered). The
     working fix is a **raw coordinate click** (`page.mouse.click(x, y)`), reading the
     click point directly off a screenshot — see `04-...png`/`06-...png`. Locator-based
     `.first`/`.last`/`.nth()` on "Manual download" are unreliable once the modal is
     open because the match count and DOM order don't correspond to visual stacking
     order in any predictable way (observed count going from a real 3 matches down to
     2, with `.last` resolving to a *background* button, not the modal's).
  4. **The actual file download can auto-fire without another click** once you reach
     the "Your download is starting... 5 second delay" screen (`09-...png`) — but the
     event is easy to miss if you're not already listening via
     `page.expect_download()`/`page.wait_for_event("download")` at the moment it
     fires. If a wait for the event times out, **don't assume failure — check for a
     "Download didn't start? Start download manually" link on the same page** and
     click that (wrapped in `expect_download`) as the reliable fallback, rather than
     reloading and restarting the whole multi-step flow from scratch.

  **Full click sequence per mod file** (see screenshots 03→09 in order):
  1. Navigate to `https://www.nexusmods.com/<game>/mods/<id>?tab=files`.
  2. Click the file card's **"Manual download"** button (normal locator click works
     for this one — it's not in the shadow root).
  3. A "Download mod file" confirmation modal opens containing a second "Manual
     download" button — **this one requires a coordinate click** (gotcha #3).
  4. This navigates to a **Free vs. Premium** choice page
     (`file_id=<n>` in the URL) — click the **"Slow download"** button (normal locator
     click works here).
  5. A **"Download this large file without losing progress"** dialog may appear for
     files over ~500MB — choose **"Standard download"** (locator click works).
  6. Lands on a **"Your download is starting"** page with a **5 second delay** and a
     free-tier speed estimate — the actual download fires automatically; catch it with
     `expect_download`/`wait_for_event`, falling back to the **"Start download
     manually"** link (gotcha #4) if the event isn't caught in time.
  7. `download.save_as('tmp/mod_downloads/<suggested_filename>')`. Note
     `download.save_as()` blocks until the (throttled, ~1.5MB/s free-tier) download
     fully completes — a 683MB file took several minutes; this is normal, not a hang.

  All ~19-21 Nexus mod pages in this build (1192, 1209, 1282, 1360, 1364-1370, 1384,
  1493, 1632, 1666, 1710, 1711, 66, 90) should be run through this same sequence now
  that it's proven working — none of them need to be logged as blocked-pending-user-
  action anymore, since the one genuinely user-only step (logging in) is already done
  for this session's profile directory.

## mega.nz (14 mods)

- Requires a real browser: MEGA decrypts the file client-side in JS from the URL
  fragment (the part after `#`), so `curl` alone can't produce a usable file even if it
  gets a 200.
- **Recommended flow:** navigate to the link with `claude-in-chrome`, wait for the page
  to reach a "ready to download" state (it shows a spinner/progress while decrypting
  metadata), then trigger the download button and capture the browser download event.
- 2026-07-30: no dated notes yet — append after first MEGA batch.

## github.com (3 mods)

- Plain release assets — no browser needed. `curl -L <url> -o <dest>` (or
  `gh release download` if the link is a repo rather than a direct asset URL) is
  sufficient.

## drive.google.com / pastebin / gamefront / misc (~5 mods)

- Rare enough not to generalize. Handle each with a `claude-in-chrome` navigation and a
  manual look at what the page actually offers (Drive files need the "Download anyway"
  click-through for files without a virus scan; pastebin links are usually just
  supplementary notes, not archives — check before treating a pastebin URL as a
  download source at all).

## Batch install pass — critical gotcha (2026-07-30)

**Do not wrap the `install -d ...` command in a short external `timeout` (e.g. `timeout
280 dotnet run ...`).** The CLI resolves/downloads ALL components in the instruction
file (189 in the K1 full build) before it starts copying anything into `Override/` —
it is not incremental per-mod. A `timeout 280` (or any timeout shorter than the full
download pass takes) kills the process before it ever reaches the actual install step,
so `Override/` stays empty no matter how many archives are already staged in
`--source-dir`. Confirmed by watching `run5.log`: it was still mid-download on
component ~170/189 when the external timeout fired.

**Fix:** launch it as a genuinely long-lived background process with no external
`timeout` — `nohup ... -- install ... -d --download-timeout-hours 72 < /dev/null >
log.txt 2>&1 &` (redirect stdin from `/dev/null` explicitly; do not rely on `nohup`
alone to detach stdin) — and let it run for however long it actually needs (can be
30+ minutes for a build this size, longer if DeadlyStream/Nexus are slow). Check
progress via the log tail and `find .../Override -type f | wc -l`, not by assuming a
fixed duration.

A separate, parallelizable option for downloading without triggering the install
phase at all: `convert -i <toml> -o <discard.toml> -d --source-path <staging-dir>
--non-interactive --fomod-skip` — this only downloads, never touches the game
directory, so it's safe to run concurrently with other work (but NOT concurrently with
another process downloading into the *same* staging directory — two writers to the
same target filename can race/corrupt a partial file).

Also observed: running `install` **without** `-d` (local-files-only, to fold an
already-staged batch into Override without re-attempting network) got stuck
indefinitely at "Starting installation..." with zero read-byte progress in
`/proc/<pid>/io` for 3+ minutes (real hang, not slow disk — two samples 5s apart
showed identical `rchar`). Root cause not fully isolated; workaround is to always run
the combined `-d` form (already-staged files are recognized via
`DownloadCacheService`/`FindBestMatchingFilenameAsync` pattern-matching against each
component's instruction `Source` globs, so re-running with `-d` does not re-download
files that already exist and pass matching — it's cheap to always include `-d`).

## Git-based checkpoint system can make installs impractically slow on spinning/USB disks

**2026-07-30:** `install` (without `--no-checkpoint`) creates a git repository inside
the game directory and commits a full baseline snapshot before doing any real install
work — logged as "Initializing Git-based checkpoint system..." / "Creating baseline
checkpoint of game directory...". For a ~6.4GB KOTOR install on a slow external/USB
drive, this measured at roughly **1 MB/s sustained write** (`/proc/<pid>/io` `wchar`
delta sampled across 5s), i.e. **~1-2 hours just for the baseline checkpoint**, before
any mod is actually copied into `Override/`. **The `--no-checkpoint` CLI flag does NOT
currently work — confirmed by reading the source.**
`src/ModSync.Core/CLI/ModBuildConverter.cs` declares a `NoCheckpoint` bool property on
the install options, but nothing downstream ever reads it: grepping the whole
`src/ModSync.Core` tree for `NoCheckpoint` turns up only that one declaration.
`InstallCoordinator.LoadOrCreateSessionAsync` (`src/ModSync.Core/Installation/InstallCoordinator.cs`)
unconditionally does `CheckpointService = new Services.GitCheckpointService(destinationPath.FullName)`
and calls `InitializeAsync()` regardless of the flag. Passing `--no-checkpoint` had
zero measurable effect in this run (re-verified: same "Initializing Git-based
checkpoint system..." / slow-write behavior with or without the flag) — this is a
real upstream bug, not a usage mistake, so don't waste a retry on it. **Current
workaround: just budget the time.** It does eventually finish and then the real
per-mod install work proceeds normally. Verify the checkpoint is actually the
bottleneck (not a real hang) by sampling `wchar` in `/proc/<pid>/io` for the process a
few seconds apart — if it's climbing, it's working, just slowly.

## Cross-mirror filename false-positive match (ComponentValidationService)

**2026-07-30:** When a component lists multiple alternate download URLs for
*different, unrelated* mods that happen to share a name prefix, ModSync's
`ComponentValidationService` can wrongly conclude a pattern is "satisfied by existing
file with different extension or naming" when it isn't. Concretely: the component
"Carth Onasi and Male PC Romance" (expects `Carth Onasi and Male PC Romance.7z`, an
installer) was reported as satisfied because `Carth Onasi.rar` — a completely
different mod (Vurt's character retexture) — already existed in the staging
directory from an earlier, unrelated component. **Don't trust "Pattern satisfied by
existing file with different extension or naming" log lines at face value** when two
components share a name prefix; grep the staging directory for the *exact* expected
filename from the component's `Directions`/`Source` field, and if it's not there,
fetch it manually rather than assuming the fuzzy match was correct. Fixed in this run
by downloading the real file directly from its DeadlyStream file page.

## Stale install_session.json blocks retries even after the missing archive appears

**2026-07-30:** `<gameDir>/.modsync/install_session.json` persists a per-component
`State` (Pending/Running/Completed/Failed/Blocked/Skipped) **across separate `install`
invocations**, independent of `--no-checkpoint` (that flag only affects the git
checkpoint system — this is a different persistence mechanism, `CheckpointManager`).
Once a component is marked `Blocked`/`Skipped` (because its archive wasn't in the
staging directory at the time), **later runs skip it again immediately — logged as
"Skipping 'X' (blocked by dependency)" — even after you've since downloaded the missing
file into the staging directory.** This is misleading: the log message says "blocked by
dependency" but the real cause is just a stale cached state, not an actual unmet
dependency (verified by cross-referencing the same component's very first failure,
which says "mod file(s) not in workspace"). **Fix: delete
`<gameDir>/.modsync/install_session.json` before any install run where you've added
new files to the staging directory since the last run** — this forces a full
fresh re-evaluation of every component. Cost: a full re-run re-walks and
re-hashes/extracts everything (confirmed component-idempotent — already-placed files
get "already exists, skipping" — but the full pass through 189 components with the
current staging directory size took **~80 minutes** on this environment's storage), so
budget accordingly. This is a real gap in ModSync worth fixing upstream (state should
be invalidated per-component when its previously-missing source file becomes
available, not just globally never-rechecked), but the workaround unblocks progress in
the meantime.

## DeadlyStream: some files need a second "confirm" step beyond csrfKey

**2026-07-30:** For roughly half the DeadlyStream files in this build, the
`?do=download&csrfKey=...` request (see recipe below) does NOT return the file directly
— it returns another HTML page containing a second link with extra params:
`?do=download&r=<n>&confirm=1&t=1&csrfKey=<key>`. This is IPS/Invision's "large file /
please confirm" interstitial. **Detect it by checking the response `Content-Type`
header** (`text/html` means you got an interstitial, not a file) rather than assuming
success from a 200 status. When that happens, grep the response body for
`do=download[^"']*confirm=1[^"']*` (unescape `&amp;` to `&`), then issue that exact URL
as a follow-up request with the same cookie jar and `Referer` — that one returns the
real file. Confirmed working for `HD Realistic Sand People`, `High Quality Skyboxes
II`, `Rebalanced Grenades`, and others; some files (`Darth Malak's Lightsaber`,
`Republic Soldier Fix`) return the file directly on the first `csrfKey` request with no
confirm step needed — always check both paths rather than assuming one or the other.
This two-step flow is pure `curl`, no browser needed, and is generally reliable for
DeadlyStream files that failed via ModSync's own `DeadlyStreamDownloadHandler` (which
apparently doesn't implement this second confirm hop) — before concluding a
"mod file(s) not in workspace" skip is a dead link, retry it manually with this
two-step curl recipe first.

## DeadlyStream download links need a real session, not a bare GET

**2026-07-30:** A bare `curl <file-page-url>/?do=download&csrfKey=...` (no cookie jar,
default curl UA) returns **HTTP 403** even when the file page itself (and the
csrfKey extraction) succeeded — DeadlyStream's forum software (IPS/Invision) appears
to require a real browser-like User-Agent plus a cookie jar carried over from the
initial page GET, and a `Referer` header pointing back at the file page. Working
pattern (plain `curl`, no browser needed):
```
curl -sL -c cookies.txt -b cookies.txt \
  -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36" \
  "<file-page-url>" -o page.html
csrf=$(grep -o 'do=download[^"]*csrfKey=[a-f0-9]*' page.html | head -1 | grep -o 'csrfKey=[a-f0-9]*' | cut -d= -f2)
curl -sL -c cookies.txt -b cookies.txt \
  -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36" \
  -e "<file-page-url>" \
  "<file-page-url>?do=download&csrfKey=$csrf" -o output.7z
```

## Archive integrity check (apply to every downloaded file, any host)

Before counting a download as successful:

1. File exists and is non-trivially sized (a few KB floor; tune per known-small
   legitimate mods rather than a single global number).
2. For archive extensions (`.zip`, `.rar`, `.7z`): confirm it actually opens as that
   archive type (`unzip -tq` for zip, `7z t` for 7z, **`unrar t` for rar — NOT `7z
   t`**) rather than being an HTML error/login page saved under an archive extension.
   The 2026-07-30 run's `Canderous Patch.rar` (104 bytes, recurred more than once across
   retries — DeadlyStream appears to intermittently serve a truncated file for this
   specific mod) is the canonical failure case this check catches — a real archive of
   that size is implausible, and testing it (`unrar t`) reports "Cannot open the file
   as archive".
   - **2026-07-30: this environment's `7z` (7-Zip 26.02) has no RAR codec built in**
     (standard for Linux 7-Zip builds — RAR support is a separate non-free codec).
     Running `7z t some.rar` reports "Cannot open the file as archive" for EVERY
     `.rar`, even perfectly valid ones — this is a false positive from the tool, not a
     real integrity failure. Use `unrar t <file>.rar` instead (a real `/usr/bin/unrar`
     was present in this environment); only trust `7z t`'s verdict for `.7z` files.
   - Also: `file` misidentifies some valid mod `.zip` archives as "Microsoft OOXML"
     (zip-based Office formats share the same magic bytes as plain zip). Don't reject
     a `.zip` on `file`'s label alone — confirm with `unzip -tq` or `unzip -l`, which
     correctly listed real contents for the one case seen (`KOTOR1-Republic-Soldier_v1.4.0.zip`).
3. Only after passing both checks does the file get left in the staging directory for
   the batch install pass to pick up.

## Stale checkpoint state blocks retries even after files become available

**2026-07-30:** `.modsync/install_session.json` (in the game directory) persists a
per-component `InstallState` (`Pending`/`Running`/`Completed`/`Failed`/`Blocked`/`Skipped`).
Once a component fails or gets marked `Blocked` (e.g. its archive wasn't staged yet at
that time), **re-running `install` does not re-evaluate it** — the coordinator replays
the cached state from this file and just logs "Skipping '<name>' (blocked by
dependency)" every time, even after the missing file has since been downloaded. Confirmed:
~36 components stuck this way across several re-runs despite their archives landing in
`tmp/mod_downloads/`. Fix: delete (or reset the relevant entries in)
`<game-dir>/.modsync/install_session.json` to force a fresh full re-evaluation, then
re-run `install`. The `State` enum (`ModComponent.ComponentInstallState` in
`src/ModSync.Core/ModComponent.cs`) maps `0=Pending 1=Running 2=Completed 3=Failed
4=Blocked 5=Skipped` if you need to reset only specific components' `State` field to `0`
rather than deleting the whole session (the latter forces the baseline checkpoint's
per-component completion tracking, not the file checkpoint itself, to be redone — cheap
compared to the 8GB+ git baseline, which is untouched).

## Canonical instructions vs. the automated TOML

**2026-07-30:** the `mod-builds` GitHub repo's own README defers to a **canonical,
human-authored install guide** at https://kotor.neocities.org/modding/mod_builds/k1/full
(saved in full at `docs/knowledgebase/kotor1-full-build-canonical-guide.md`) for exact
per-mod install steps — specific file deletions, folder-only selections, and
install-order/master-mod notes. **The merged instruction TOML does not always encode
these precisely.** Spot-checking found it inconsistent: some components (e.g. "Taris
Reskin") correctly encode the guide's exact multi-file deletion list; others (e.g.
"Ultimate Taris High Resolution", "NPC Clothing M") only have a blanket Extract+Move
with no Delete/rename step the guide requires, leaving extra files in Override that
cause guide-documented visual bugs. When a component's behavior looks "successful" but
seems off, **check its entry in the canonical guide before assuming a ModSync bug** —
the automation may just be faithfully executing an incomplete instruction. A full
line-by-line audit of all 136 top-level mods against the guide has not been done; treat
this as a known gap for a future pass, not a solved problem.

## Real ModSync bug: merge step corrupts a duplicate dependency GUID into a phantom one

**2026-07-30:** "Ultimate Character Overhaul Patches" permanently failed its dependency
check every run, even after a full checkpoint reset ruled out staleness. Root cause: in
the **source** `mod-builds/TOMLs/KOTOR1_Full.toml`, this component's `Dependencies` list
is `["63cf4877-...", "eff1eb51-...", "63cf4877-..."]` — the third entry is a harmless
duplicate of the first. In the **merged** `tmp/KOTOR1_Full_merged.toml` produced by
ModSync's own merge step, that third entry becomes `"92c3a209-055c-4061-8af9-7a040f597597"`
— a GUID that does not correspond to any component anywhere in the merged file. This is
a genuine bug in `src/ModSync.Core/Services/ComponentMergeService.cs`, likely around the
`HashSet<Guid>` dependency-dedup logic (~line 757) interacting with `Guid.NewGuid()`
generation elsewhere in the same file (~line 979) — plausibly the dedup path
mishandles an exact duplicate and falls through to a fresh-GUID-generation branch meant
for genuinely new/unmatched entries. **Not yet fixed in the C# source** — worked around
for this install by editing the merged TOML directly (removed the phantom third entry).
If this recurs on a re-merge, re-apply the same fix or dig into `ComponentMergeService.cs`
directly; this is worth a proper source-level fix in a follow-up session since silent
dependency corruption could affect other duplicate-GUID cases in any build.

## The install command must be allowed to run to completion, uninterrupted

**2026-07-30: do not wrap the `install` command in a short external `timeout`.**
Six consecutive install invocations against the full 189-component `KOTOR1_Full`
build each ran under an external `timeout 280` (~4.7 minutes) and were killed before
`Override/` ever received a single file, despite `tmp/mod_downloads/` correctly
accumulating 120+ verified archives across those same runs. The CLI's `install -d`
appears to resolve/attempt every component's download before it reaches the actual
copy-into-`Override` step — it is not incremental per mod within a single invocation.
A 280s external timeout guarantees the process is killed mid-resolution on a
189-component build long before it would reach that step, no matter how much is
already staged. **Run the install command as a genuinely long-lived process with no
short external timeout** (the command already carries its own
`--download-timeout-hours 72` internal budget) — either a real background process or
an external timeout measured in hours, not minutes. Confirm progress by watching for
the log to actually reach a copy/extract/install stage, not just download-resolution
lines for individual components.

## nexusmods.com / gamefront.com: CLI auto-download hard-fails, needs a manual drop-in

Confirmed from a full run's log: Nexus Mods returns HTTP 401 via the CLI's own
`HttpClient` path ("Free downloads from Nexus Mods require manual interaction...
provide an API key"), and GameFront fails with "requires manual interaction... JS
countdown timers and anti-bot protection." Neither can be fetched by the CLI's
automatic downloader at all — this isn't a transient failure to retry. For both, the
working path is: download via `claude-in-chrome` (per the host sections above), then
place the resulting archive into the staging directory under whatever filename
convention `DownloadCacheService`/`InstallationService` uses to recognize a component's
file as already present (check `src/ModSync.Core/Services/Download/` and
`src/ModSync.Core/Services/InstallationService.cs` for the exact matching logic if the
naming isn't obvious from the component's own `Filename`/`Destination` field) — once
recognized as present, the CLI skips the failing auto-download path for that component
entirely.

## Batch install pass (once a batch of verified archives is staged)

```
dotnet run --project src/ModSync.Core/ModSync.Core.csproj -f net9.0 --no-build -- \
  install -i <merged-toml> -g <real-game-dir> -s <staging-dir> \
  -d --concurrent --best-effort --skip-validation --download-timeout-hours 72
```

Run this after each meaningful batch of new downloads rather than one mod at a time —
downloading is the slow part, installing is fast and idempotent under `--best-effort`.
