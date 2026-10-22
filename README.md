# Sky Islands: Voyage

A dice-roll island builder: 50-roll energy, multipliers, a pity-timed probability matrix,
200 islands across 8 biomes, async raids with Cloud-Shields and revenge, a seasonal card
album, guilds with live chat, store purchases and push notifications.

```
supabase/
  schema.sql        tables, RLS, all game RPCs (authoritative economy)
  seed.sql          sample card season + store catalog
  tests/            SQL test suites + run.sh (throwaway local Postgres); golden-vector generator
server/             NestJS service: store receipt verification, push delivery, live-ops admin
  src/iap/          Apple App Store Server API + Google Play verification -> grant_purchase
  src/push/         drains notification_outbox into Firebase Cloud Messaging
  src/admin/        anomalies, grants, report queue, card seasons, store catalog (X-Admin-Key)
  Dockerfile        multi-stage, non-root, health-checked image
Unity/Assets/SkyIslands/
  Scripts/Core/          GameEconomyManager (EconomyRules, regen, sync), DiceRollEngine, SecureSave
  Scripts/Progression/   IslandProgressionController (state machine), IslandSceneView, BiomeDatabase
  Scripts/Multiplayer/   RaidAndShieldService, GuildService
  Scripts/Cards/         CardAlbumService
  Scripts/Backend/       SupabaseClient (auth + RPC), SupabaseRealtime, GameServicesClient (IAP, push)
  Scripts/UI/            MVVM view models + views (UI Toolkit)
  Scripts/SkyIslandsBootstrap.cs   composition root
  Scripts/SkyIslands.asmdef        runtime assembly "SkyIslands"
  Tests/EditMode/        NUnit tests incl. GoldenVectors.cs generated from the SQL
  UI/                    MainScreen.uxml + panel templates, Theme.uss
  Resources/biomes.json  8 biomes covering islands 1–200
  link.xml               keeps JSON DTOs through IL2CPP stripping
docs/ECONOMY.md          pacing analysis (campaign length, income mix, tuning levers)
tools/economy-sim/       Monte Carlo pacing simulator (Python, stdlib only)
tools/unity-compile/     compiles the Unity scripts on plain .NET (Unity API stand-ins) + runs the tests
```

## Architecture

**The server is authoritative, and rolls still work offline.** Each roll's outcome is
`SHA-256("<seed>:<index>")` mapped onto integer weights, so it's deterministic. The client
resolves rolls instantly (also offline) and queues `(index, multiplier, timestamp)`.
`sync_rolls` replays the queue on the server, re-checks the regen budget against clamped
timestamps, and returns the authoritative state. The client then replays any rolls taken
while that request was in flight. A modified client can't invent rewards, roll faster than
regen allows, or borrow future regen by moving its clock forward.

Building, repairing, raiding, cards, guilds and purchases need a connection. Each of these
RPCs syncs pending rolls first, then mutates state server-side with row locks taken in a
fixed order: `inventory_rolls → raid_history → profiles → island_states`.

**Raids.** Clients never choose victims. `perform_raid` looks for a target in two passes:
1. a player within ±5 island levels who was active in the last 14 days;
2. otherwise, a player within ±15 levels who was active in the last 30 days.

Anyone the attacker hit in the last 30 minutes is skipped. If both passes come up empty,
the cannon hits an **NPC Sky Pirate outpost** instead (25% chance it's shielded, no
defender, no revenge). That way a new game with few players still works, and a level-150
player is never matched against a newcomer they'd wipe out.
- If a real target has a Cloud-Shield, the shield is consumed, 80% of the raid is blocked,
  no structure is damaged, and the target gets no revenge entry.
- Otherwise, the target's most valuable intact structure goes into the Damaged (smoke)
  state, the target loses crystals, and they get a high-priority `revenge` notification.

Defenders get the raid in-game over Supabase Realtime (RLS-checked). A trigger also queues
it in `notification_outbox`, which the NestJS worker delivers as a push notification.

**Purchases.** The client tags each store purchase with the player's id (Apple
`appAccountToken` / Google `obfuscatedAccountId`), then calls `POST /iap/verify` on the
NestJS service.
- **Apple:** the server looks the transaction up with the App Store Server API and
  verifies Apple's signature chain on the result.
- **Google:** the server checks the purchase with the Play Developer API.

In both cases the server then calls `grant_purchase`, which is idempotent per
`(store, transaction_id)` and rejects receipts that belong to another player. Google
purchases are consumed only after the grant is committed.

**Guilds.** Up to 30 members, with leader, officer and member roles. If the leader leaves,
the longest-serving officer takes over (or member, if there are no officers); the last one
out disbands the guild. Chat messages are rows in `guild_messages`, and members receive
them over Realtime. RLS gates the feed, so a removed member stops receiving immediately.
Chat is rate-limited to 5 messages per 10 seconds.
Closed guilds aren't listed; players join them with an 8-character invite code.
- Leaders and officers can see the code, generate a new one, and open or close the guild.
- The code isn't readable through the `guilds` table, which has column-level grants.

**Reports.** Players can report another player's name from the roster, or tap any chat
message from another player to report it. A message report attaches that message as
evidence (only allowed for a message in the reporter's own guild). The report keeps a copy
of the message, so the evidence survives if the message is later deleted.
- Each player can file at most 10 reports a day, and duplicate open reports are ignored.
- Moderators triage the queue through the admin API. Resolving a report can also reset the
  player's name to "Voyager NNNN" and delete the reported message; both actions are audited.

**Moderation.** Display names (3–20 characters, any script, no markup or symbols) change only
through `set_display_name`. Names and guild names are checked against `banned_name_terms`,
ignoring case, spaces and punctuation, so "A.d m-i n" is still caught. Guild chat masks those
terms with `*`. The defaults only block impersonation (admin, moderator, official,
developer); add your own list.

**Anti-cheat.** Offline rolls need the seed on the device, so a modified client could peek
at upcoming outcomes and raise the multiplier only before good ones. That can't be
prevented without giving up offline play, so it's detected instead:
- `sync_rolls` tallies non-Crystal ("premium") results separately for ≥10x and ≤2x rolls,
  in `roll_audit`, a table players can't read.
- `admin_multiplier_anomalies` runs a two-proportion z-test on those tallies. Honest players
  have the same premium rate at every multiplier.
- In the test suite, a simulated peeking client scores z ≈ 39 and an honest player
  stays near 0 (|z| between 0.7 and 1.03 over three runs). The default flag threshold is
  z ≥ 4.

**Live-ops.** The NestJS `/admin` API needs an `X-Admin-Key` header and an `X-Admin-Actor`
header, and the actor is written to `admin_actions` with every change. It has two endpoints:
- `GET /admin/anomalies` returns the anti-cheat report.
- `POST /admin/grants` gives compensation. It's bounded to 100,000 rolls and 1,000 packs
  per grant, and a reason is required.
- `GET /admin/seasons`, `POST /admin/seasons`, `DELETE /admin/seasons/:id` manage card
  seasons without hand-written SQL.
  - A season is created whole (sets and cards), in one transaction.
  - Seasons can't overlap, and can't be edited once created, because players' albums
    reference their cards.
  - They can be deleted only before they start.
- `GET /admin/products`, `PUT /admin/products/:productId` manage the store catalog. Use
  `active: false` instead of deleting a product, because purchases reference it.
- `GET /admin/reports?status=open` lists the report queue, oldest first.
- `POST /admin/reports/:id/resolve` closes a report, with optional `resetName` and
  `deleteMessage`.

## Design decisions the spec left open

| Topic | Choice |
|---|---|
| Cost curve | `Cost(n) = floor(25,000 × 1.2^(n−1))`, computed as `6^(n−1)/5^(n−1)` integer math so C# and SQL match exactly. Cost(200) ≈ 1.43 × 10²⁰, which overflows 64-bit, so crystals are `numeric(40,0)` / `BigInteger` and travel as strings. |
| Structures | 5 per island × 4 tiers. Slot weights 14/17/20/23/26%, tier weights 15/20/25/40% of Cost(n). Repair = 50% of the current tier. |
| Roll multiplier | Spends `m` rolls; every outcome scales linearly by `m`, so expected value per roll is the same at every multiplier. Only affordable multipliers can be selected. |
| Outcomes | Crystals = `250 × 1.2^(n−1) × m`. Wind-Cannon = one raid at `m`× loot (10 × roll reward). Cloud-Shield = +1 (max 3; once full it pays crystals instead). Storm Spirit = `m` card packs. Aether Battery = `2m` rolls, which can exceed the 50 cap. |
| Pity | A roll is "dry" if it's neither Wind-Cannon nor Cloud-Shield. After 15 dry rolls, those two weights get ×1.2 (25→30, 15→18; the table is renormalized over 108) until one lands. |
| Near-miss | Presentation only: some Crystal results are drawn as `[X, X, Crystal]`, and the UI holds the last face a beat longer. The reward never changes. Tune it with `nearMissChance` (0 disables it). |
| Revenge | Only unshielded raids by real players grant revenge, as the spec literally says. Revenge raids can be revenged in turn. |
| Cards | 3 weighted draws per pack. A duplicate becomes `10 × rarity` dust. `craft_card` turns `50 × rarity` dust into a chosen missing card. Completing a set pays rolls + crystals scaled to the player's island. |
| Store | `store_products` maps a store product id to rolls + card packs (examples in `seed.sql`). Only consumables, quantity 1. |

## Setup

1. **Supabase:** enable *Anonymous sign-ins* (Auth → Providers). Then run
   `supabase/schema.sql` and optionally `supabase/seed.sql` in the SQL editor, or copy
   `schema.sql` into `supabase/migrations/` and run `supabase db push`.
2. **Server** (`server/`, Node 20+):
   1. Copy `.env.example` to `.env`. Supabase settings are required; the Apple, Google and
      Firebase sections are each enabled only when fully set.
   2. Install and build:
      ```bash
      npm ci
      ```
      ```bash
      npm run build
      ```
      Then start the server with `npm start`.
   3. For Apple, download Apple's root certificates (e.g. `AppleRootCA-G3.cer`) into
      `APPLE_ROOT_CERTS_DIR`.
   4. Or run it as a container (secrets are mounted, never baked into the image):
      ```bash
      docker build -t sky-islands-server server/
      ```
      ```bash
      docker run --env-file server/.env -v /path/to/secrets:/secrets:ro -p 3000:3000 sky-islands-server
      ```
      Point the `*_PATH` / `*_FILE` variables at `/secrets/...`.
3. **Unity 6 (URP):** copy `Unity/Assets/SkyIslands` into your project's `Assets/` and add
   the package `com.unity.nuget.newtonsoft-json`. Set Api Compatibility Level to
   .NET Standard 2.1.
4. Create a *Biome Database* asset (Create → Sky Islands → Biome Database). Assigning
   `biomes.json` is optional; it's loaded from Resources if left empty.
5. **Scene:**
   1. Put `GameEconomyManager`, `IslandProgressionController`, `RaidAndShieldService` and
      `SkyIslandsBootstrap` on one object and wire their references.
   2. In the bootstrap, enter the project URL, the **publishable/anon key** (never the
      service-role key), and the NestJS server URL.
6. **UI:**
   1. Create a *Panel Settings* asset: Scale With Screen Size, reference 1080×1920,
      match 0.5. It needs a theme style sheet; Unity creates one with the asset.
   2. Add a `UIDocument` that uses `UI/MainScreen.uxml`, and a `SkyIslandsUI` component
      pointing at the bootstrap.
7. **Purchases & push:**
   - Hook Unity IAP's purchase callback to `bootstrap.GameServices.VerifyApplePurchaseAsync`
     / `VerifyGooglePurchaseAsync`, and finish the transaction only after it succeeds.
   - Pass Firebase Messaging's token to `RegisterPushTokenAsync`.
   - Create an Android notification channel named `raids`.
8. **World:** add `IslandSceneView` with 5 slot anchors and optional smoke and celebration
   particle prefabs.
   - Structure prefabs are named `<biome id>/s<slot>_t<tier>` (tier 0 = scaffolding) and
     `<biome id>/s<slot>_damaged`.
   - With `com.unity.addressables` installed they load as Addressables under those keys;
     otherwise from `Resources/Structures/`.
9. **Tests:**
   - Unity: Window → General → Test Runner → EditMode → Run All (needs
     `com.unity.test-framework`).
   - SQL suites: `bash supabase/tests/run.sh`.
   - Without Unity (e.g. in CI): `bash tools/unity-compile/run.sh` (needs the .NET 10 SDK).
   - After changing any economy rule, regenerate the Unity golden vectors:
     ```bash
     GOLDEN_OUT=Unity/Assets/SkyIslands/Tests/EditMode/GoldenVectors.cs bash supabase/tests/run.sh
     ```

## Verification status

- **CI:** GitHub Actions (`.github/workflows/ci.yml`) runs on every push and pull request:
  - the SQL suites;
  - a check that `GoldenVectors.cs` matches the current SQL;
  - the server's typecheck, tests and build;
  - the Unity compile and EditMode tests;
  - the server's Docker image: it builds, boots, answers `/health`, keeps the admin API
    locked without the key, and doesn't run as root.
- **SQL:** `schema.sql` was run against PostgreSQL 18 with a stub of Supabase's `auth`
  schema and roles. Six suites pass (`supabase/tests/`). They cover:
  - RLS isolation, column grants, anon lockout, and service-role-only RPCs;
  - idempotent and rejected roll syncs, regen and clock clamping;
  - island completion, shielded and unshielded raids, NPC fallback and the fair-match
    windows, the revenge loop and repairs;
  - card packs, set completion and crafting;
  - the push outbox (claim, lease retry, dead-token pruning);
  - purchase idempotency and stolen-receipt rejection;
  - guilds: roles, kicks, succession, disbanding, the 30-member cap, chat RLS and the rate
    limit;
  - name and chat moderation;
  - admin grants and their audit trail;
  - an end-to-end cheater simulation through `sync_rolls`;
  - invite codes (including privacy and the member cap), reports, evidence rules, and admin
    resolution;
  - card seasons (validation, overlap, atomic rollback, activation, delete-before-start) and
    the store catalog.
- **Client/server parity:** an independent reference implementation matched all 20,000
  roll outcomes and all 4,000 level × slot × tier prices against the SQL exactly.
- **Golden vectors:** `GoldenVectors.cs` is generated from the live SQL functions. It holds
  RNG outputs, prices at boundary levels, regen edge cases, and a 300-roll `sync_rolls`
  replay that crosses the regen cap and runs out of budget. A Python port of the C# roll
  loop reproduces that replay exactly, so the Unity tests should pass as soon as the code
  compiles.
- **Server:** it type-checks and builds. 37 Jest tests pass, including end-to-end HTTP
  tests through the real `AppModule` (player and admin guards, validation, error mapping). The calls
  to Apple, Google, FCM and Supabase are mocked; they still need a sandbox run with real
  credentials.
- **Unity C#:** all runtime scripts compile with the real C# compiler at Unity 6's level
  (C# 9, .NET Standard 2.1): 0 errors and 0 warnings, with warnings treated as errors, both
  with and without Addressables.
  - Unity's own assemblies can't be redistributed, so the build uses stand-ins written to
    match Unity 6's signatures for the APIs the game uses (`tools/unity-compile/Stubs`).
  - All 28 EditMode tests pass on NUnit 3. That includes the golden replay: offline rolls
    reproduce the server's `sync_rolls` state exactly.
- **Not yet checked in a real Unity editor:** UXML/USS import, asmdef resolution, IL2CPP,
  and Play-mode behaviour such as chat auto-scroll. Three code reviews covered these; the
  bugs they found are fixed.

## Not included / next steps

- Art: structure prefabs, VFX and audio. Also the Unity IAP / Firebase SDK glue described
  above.
- An admin UI: the `/admin` API is curl-able today.
- **Docker image not yet built by me:** Docker wasn't running on the build machine. I checked
  the same steps by hand (production-only install, compiled output boots, routes respond);
  the CI job does the real image build.
- **Remaining anti-cheat gap:** the detector can't flag a cheater who peeks but only rarely
  acts on it. Each such roll gains little, but if it matters, move high multipliers to
  online-only rolls.
- Showing drop rates is required or expected in several markets (e.g. China, South Korea,
  Apple's guidelines for paid random items) if rolls are sold for real money.
