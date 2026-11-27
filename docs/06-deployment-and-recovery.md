# Family Librarian — Deployment, Backup, and Recovery

Family Librarian runs as three Compose services:

```text
postgres             persistent PostgreSQL data
migrate              one-shot EF Core migration job
family-librarian     ASP.NET Core API and hosted Blazor WebAssembly application
```

`migrate` must finish successfully before `family-librarian` starts. It uses the
same application image with the `--migrate` command, so the schema reviewed in
source control is the schema deployed by Compose. Do not replace it with
`EnsureCreated`, and do not run migrations from every application replica.

## Project Gutenberg catalogue refreshes

On **Sources**, **Refresh now** queues a server-owned catalogue refresh and returns
immediately. Closing the browser does not cancel accepted work. Manual, startup
and daily checks share the same importer and ordered fallback sources for the full
archive, daily feed and individual book records. A gap beyond the daily feed's
coverage triggers a full import; the newest successful full or incremental check
determines that gap.

The page reports the current phase, source host, download bytes and import counts.
A download that receives no bytes for two minutes fails with an explanation;
active transfers may run longer. The previous validated snapshot remains searchable
until a replacement is validated. A refresh cannot overlap another refresh or a
catalogue deletion. On application startup, queued work is picked up, while an
operation interrupted after starting is marked failed with a retry instruction.
No manual database status reset is needed after upgrading from a stuck refresh.

## Resumable audiobook archive downloads

An in-progress LibriVox audiobook archive is kept under
`Storage__RootPath/acquisition-work/librivox/<request-format-id>` as one
partial ZIP plus a small sidecar containing the provider result, final download
URL, entity validator, and expected byte count. In the default Compose setup,
`Storage__RootPath` is `/data/family-librarian`, backed by the
`family-librarian-data` volume. Keep that volume mounted across container
recreation; the partial archive is not stored in PostgreSQL or the container's
temporary directory.

After restart, Family Librarian sends an HTTP byte-range request from the saved
file length and uses `If-Range` with a strong ETag or Last-Modified value. It
appends only when the response confirms the requested range and matching
representation. If the source redirects to a different mirror, ignores the
range, changes the archive, or provides no stable validator, Family Librarian
discards the partial file and downloads the archive from the beginning. A
completed archive remains available through extraction and quarantine staging.
It is removed after staging finishes, including a handled rejection. A
connection interruption, retryable server response, or host shutdown releases
the lock and retains the ZIP for the next attempt. Other failed attempts and
invalid or corrupt archives are deleted immediately. Any leftovers from an
unclean process termination are pruned at startup after 30 days.

Range support varies by final download host, so a particular transfer may safely
restart from zero even though the resume path is implemented. A download that
receives no bytes for two minutes is treated as interrupted; automatic
fulfillment schedules a retry while the request remains pending. A completed
ZIP is re-used after restart and is fully read during extraction before it is
accepted.

## Disk space and automatic clean-up

Family Librarian cleans up after itself so an installation that runs unattended does not fill its disk. Administrators can
see the result on **Storage** (free space, files by stage, files held and why, files on disk no record accounts for, and
database table sizes). The page shows no book titles or file names and deletes nothing.

A clean-up pass runs about every six hours. It removes only files nobody needs:

| Kept for | Removed after |
| --- | --- |
| Rejected (failed a security check) | 14 days |
| Held copy the automatic search has already moved past | 7 days |
| Held ebook waiting for a person | 30 days |
| Held audiobook waiting for a person | 14 days |
| Received or being checked, never finished | 7 days |
| File with no record at all (not in the trusted zone) | 24 hours after it appeared |

Copies waiting for a person are also capped at 10 GB in total; beyond that the oldest go first, audiobooks before ebooks.
Administrators get a notification when a waiting copy is half way to removal, with the removal date, and when a copy is
removed the request is left open to another automatic search. A file that is trusted or part of a set with a trusted track
is never removed. Each removal is recorded in the audit log (counts and ids only, never names).

When free space falls below the larger of 5 GB or 10% of the volume, new downloads wait, administrators are alerted once a
day, and rejected and ruled-out copies are kept for one day instead of their usual period. Copies waiting for a person are
never removed early, because one may be the only copy.

Administrators change them on **Storage > Clean-up** (Admin menu): every period is a labelled field showing its allowed range and
default, a value outside its range is refused with the reason (never quietly changed), **Restore defaults** undoes any change, and the
same section shows when each clean-up last ran, what it did and when the next pass is due. The saved values win; until someone saves,
the configured defaults apply. The **Security queue** and the Storage page show the date a held file will be removed (by age; the size
limit or a low disk can remove it sooner, and releasing it keeps it). Operators can still set the defaults with `Storage__Retention__RejectedDays`, `RuledOutDays`, `WaitingEbookDays`,
`WaitingAudiobookDays`, `StuckDays`, `HeldBytesCap`, `OrphanGraceHours`, `BatchSize` and `PressureDays`. Values are clamped
to fixed limits (for example an ebook always gets at least 7 days) so a typing mistake cannot switch clean-up off or make
it aggressive. Container logs are rotated by the Compose file (three 10 MB files per service).

### Database clean-up

Once a day the database is trimmed of rows that only ever grow and explain nothing still in use:

| Kept for | Removed after |
| --- | --- |
| Detailed file-check evidence for a file that was removed, or for a delivered copy that a newer delivery has replaced (never the copy currently in the library, never an identity-override record, never while a review of that request is open) | 90 days |
| Advisory AI findings | 180 days |
| Per-source attempt history of a finished (in the library or cancelled) request | 180 days |
| Search wordings tried for a finished request | 30 days |
| In-app notifications and their read receipts | 90 days |
| Closed source-interaction alerts | 90 days |
| Chat or e-mail messages already sent (an unsent message is never removed) | 90 days |
| Chat message ids already handled (kept so a redelivered message is answered once; request keys are kept) | 30 days |
| Audit log | 2 years |

Open requests, files that still exist, and unresolved reviews keep all of their history. Each pass is bounded and each
table is handled separately, so one failure never stops the rest. The configured defaults (used until an administrator saves values on **Storage > Clean-up**) are
`Storage__DatabaseRetention__EvidenceDays`, `SemanticFindingDays`, `AttemptDays`, `SearchMemoryDays`, `NotificationDays`,
`AlertDays`, `CommunicationDays`, `AuditDays`, `ConversationEventDays` (configuration only, never less than 7), `BatchSize` and `MaxBatchesPerTable`; values are clamped (the audit log is
never kept for less than a year). Model usage and saved AI decisions have their own, shorter retention.

## Better copies of delivered books

A copy that was delivered but whose recorded checks showed it may be incomplete (a sample marker in its metadata, table-of-contents
or reading-order entries pointing at missing content, duplicate reading-order items, or a gap or duplicate in an audiobook's track
set) is looked at again about every 30 days. A copy a person chose (a manual upload, or a file an administrator released over a
hold) is never touched.

When a strong candidate that was not tried before turns up, administrators are notified ("A better copy of X may be available")
and, **by default**, the upgrade is carried out when the library can be changed safely:

1. The better copy is fetched and goes through every normal check.
2. It is delivered **next to** the old copy: to CWA as a second book, to Audiobookshelf under a temporary title ("Title (upgrade
   1a2b3c4d)") so it cannot merge with or overwrite the old item.
3. It is confirmed in the library. Only then is the old copy removed (CWA: the book is deleted; Audiobookshelf: the item and its
   files are deleted and the new item is given the real title).
4. Anything unexpected at any step leaves both copies as they are and notifies administrators.
   The better copy is recorded as delivered as soon as it is confirmed, before the old one is touched, and a step that is interrupted
   (for example by a restart) is simply repeated on the next pass: an old copy that is already gone counts as removed.
5. If the better copy never arrives (the source gives up, or the copy fails its checks or is removed), the upgrade is closed, the
   copy in the library stays, administrators are notified, and the book is looked at again on the next pass.
6. The system cleans up after itself: an unused extra copy a stopped upgrade left in the trusted zone is removed automatically a
   day later, and the removal is audited and noted in the request's history. Held copies removed by storage clean-up are noted there too.

It only starts when the destination can remove the old copy: for audiobooks the Audiobookshelf token must have the *delete*
permission; for ebooks the CWA e-reader service account must be configured and allowed to delete books. Otherwise the proposal
stays an alert. Administrators see every proposal on **Better copies** (Admin menu) and can start one (**Replace**) or set it aside;
the same page has the **Replace incomplete books automatically** switch. The saved switch wins; before anyone changes it,
`Upgrades__AutoReplace` (default true) applies. The API behind the page is `GET /api/v1/admin/upgrades`, `POST .../{id}/approve`,
`POST .../{id}/dismiss` and `GET`/`PUT .../settings`. With it off every proposal waits for an administrator; `Upgrades__ScanIntervalDays`
(default 30, minimum 7) and `Upgrades__BatchSize` tune the search. An item replaced in Audiobookshelf is a new item, so listening
progress on the old one is lost.

## Optional linked ebook libraries

Calibre-Web and Calibre-Web Automated (CWA) are optional integrations, not
services in the default Compose deployment. A configured Calibre-Web source is
reached over the server-side catalog/OPDS interface with a least-privilege
account; its credentials belong in the deployment's secret mechanism and are
never supplied to the browser.

CWA is the initial automated ebook-library destination. Its three main volumes
must remain distinct: CWA configuration, the Calibre library, and the watched
ingest directory. The managed Calibre library is the permanent ebook store.
Family Librarian uses only short-lived quarantine/processing/outbound storage
and gets write access only to a dedicated outbound staging location plus the CWA
ingest directory. It must not mount or write CWA's `metadata.db` directly.

The application copies a complete approved file to outbound staging and then
atomically hands it to the ingest directory. Do not download directly into that
watched directory: CWA documents that partial files can create duplicate imports
or database corruption. Disable CWA's automatic conversion, metadata rewriting,
EPUB fixing, and auto-send for the initial integration; enable them only after a
future adapter can verify the final processed result.

Back up the complete Calibre library directory (`metadata.db` and all book
folders) and CWA configuration separately from PostgreSQL. Test restoring those
volumes together before relying on CWA as the household's ebook library. On
network shares, use CWA's documented network-share mode and prove ingest and
recovery behavior in the deployment-specific compatibility test.

CWA ingest transport (local/shared filesystem, or SFTP for a CWA host that
does not share a filesystem with Family Librarian) is configured independently
of the HTTP(S)/OPDS catalog connection that Family Librarian also needs
against the same CWA instance for ownership lookup and post-ingest
verification. Both are required in every deployment topology, local or
remote: configure the OPDS base URL and account alongside whichever ingest
transport applies, and confirm the OPDS connection test succeeds, not only the
ingest connection test. A deployment with a working ingest transport but no
reachable OPDS endpoint will keep re-acquiring books CWA already has and will
never confirm that an ingested file actually imported successfully.

Audiobookshelf is the initial permanent audiobook store. Back up its persistent
configuration and library volumes separately from PostgreSQL, and test their
restore with the same care. These are initial adapters, not an exclusive list:
each future ebook, audiobook, or combined-library destination must document its
own isolated credentials, import verification, format support, and backup/
recovery procedure.

## Required malware scanner for file acquisition

ClamAV is optional only for deployments that do not acquire or accept ebook or
audiobook files. Once manual upload, a linked-library stage, or an acquisition
provider is enabled, the deployment must configure a required scanner and include
its health in the acquisition readiness check.

An unavailable required scanner fails closed: the host continues catalog search
and request creation, but rejects uploads before accepting file bytes and does
not begin provider downloads or linked-library staging. Existing requests remain
in `WaitingForSecurityScanner` for automatic, auditable backfill after scanner
health recovers. If the scanner fails during ingress, retain the affected file in
quarantine and do not publish it to CWA, Audiobookshelf, a download endpoint, or
a notification.

### ClamAV size and time limits

The shipped `compose.yaml` raises clamd's defaults so large audiobooks scan
successfully. Keep these values if you supply your own ClamAV configuration:

| clamd setting | Value | Why |
| --- | --- | --- |
| `StreamMaxLength`, `MaxFileSize`, `PCREMaxFileSize` | `2000M` | ClamAV cannot scan files over about 2 GB. Family Librarian caps accepted audiobook files at 1996 MiB (`ExternalProviderOutputPolicy`) so every accepted file is scannable. |
| `MaxScanSize` | `4000M` | Bounds total work across archive expansion; keep it above the input limits. |
| `MaxScanTime` | `600000` (ms) | The 120 s default cuts off a 1-2 GB file. Keep it under the 15-minute abandoned-scan threshold. |
| `AlertExceedsMax` | `yes` | A limit hit is reported as `Heuristics.Limits.Exceeded.*`, which Family Librarian treats as a scanner error (held for review), never as malware. |

A file that exceeds a limit, or a scan that cannot finish, stays in Quarantine
with the reason shown on **Security scans**. Administrators can use **Retry
security scan** (queued server-side, so closing the browser does not cancel it)
or **Rescan** for a scan that could not complete. A scan left pending for more
than 15 minutes is treated as abandoned and returned to Quarantine.

## Deploy or upgrade

1. Back up PostgreSQL before changing the image, Compose configuration, or
   application version.
2. Fetch the selected immutable application image/tag and the matching Compose
   configuration.
3. Supply production values through the deployment's secret mechanism. At
   minimum, set the PostgreSQL password and bootstrap credentials if the
   installation has not created an administrator yet. Never commit these
   values.
4. Start the stack:

   ```bash
   docker compose up -d
   ```

5. Confirm the migration job completed and the application is healthy:

   ```bash
   docker compose ps
   docker compose logs migrate
   curl --fail http://localhost:8080/health/live
   curl --fail http://localhost:8080/health/ready
   ```

The `migrate` service is intentionally expected to be exited with status zero.
The application waits for that successful completion and the PostgreSQL health
check. A failed migration is a deployment failure: leave the application
stopped, retain the logs and backup, and investigate before attempting another
upgrade.

EF Core records each applied migration in its migration history table, so the
one-shot job applies only pending migrations. Family Librarian tests the forward
upgrade path from the M5 request-workflow schema to the current schema. As with
all EF Core migrations, do not downgrade a production database casually: a
rollback executes migration `Down` operations and may lose data. See
[Microsoft's migration deployment guidance](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying).

### Catalog evidence migration

The catalog-evidence migration is additive: two new tables (`catalog.source_observations`, `catalog.metadata_claims`) and nullable
columns on existing catalog tables. No existing value is rewritten and nothing is back-filled, so books saved before the upgrade
simply show no provenance. Its `Down` step drops only what it added and so discards every observation and claim written since the
upgrade; after the application has written evidence, recover with a restore from backup or a forward fix, not `Down`.

Catalog writes (resolving a book, an administrator refresh, and the start-up sweep that retires orphaned Works) take a
transaction-scoped PostgreSQL advisory lock, key `(831705, 0)`, with a five-second `lock_timeout`; request creation uses a separate
lock. The lock is released by commit, rollback or the connection ending. A refresh or resolve that cannot get it in time answers
`503` and writes nothing; it is safe to try again. If a commit's reply is lost, the outcome is settled from PostgreSQL's own record of that transaction (`pg_xact_status`, PostgreSQL 13 or later; the bundled image is 17), not from timestamps; where it cannot be settled the request reports an unknown outcome instead of repeating the write. There is no scheduler: metadata is refreshed only when an administrator asks.

## Versions

The running Family Librarian version is shown in the status footer for every
signed-in user. Release images take it from the git tag (`v1.0.0-alpha.5` shows as
`1.0.0-alpha.5`, passed to the Dockerfile as the `APP_VERSION` build argument);
a local build shows the `Version` in `Directory.Build.props`, so bump that value
when you start a new release line.

Each external provider's own version, as declared by its manifest, appears as a
"Version" chip on **Sources**. It refreshes on **Test Connection** and on the
periodic background health check (about every 15 minutes), so a provider
upgrade shows up without any manual step.

## First-time setup for family members

A new member who opens the home page is offered a short **setup guide** (`/welcome`)
for format preferences, Kindle delivery and **Message the Librarian** (the member
name for [Matrix messaging](#matrix-messaging-message-the-librarian)). Every step
can be skipped.

- Only what is set up is offered. The format step appears when audiobooks are
  enabled, Kindle when [Kindle delivery](#targeted-kindle-delivery) is configured,
  and messaging when the Matrix bot connection is enabled. With nothing to offer,
  the guide does not open.
- It opens by itself once, and only from the home page; a link to any other page
  goes where it says. **Setup guide** in the settings menu reopens it.
- Afterwards, and for accounts that existed before this feature, a dismissible
  reminder mentions Kindle delivery or messaging that is available but not set up,
  including something you turn on later. Dismissals are saved to the account.

Two cards hold what members are told. Everything in them is visible to **every
signed-in user**, so never enter a password or registration token. Neither card
changes how books are sent or messages delivered.

**Settings → Publishing → Kindle setup help for family members**

- **Kindle sender address:** the From address in CWA's email settings, which each
  member approves in Amazon. While it is blank, members are told to ask an
  administrator.
- **Amazon store:** a domain such as `amazon.co.uk`, or any pasted link from the
  store; blank means `amazon.com`. Only the form of the address is checked, not
  whether Amazon runs that domain, so open the two links shown under the field
  after saving. Amazon can also move its account pages.

**Settings → Communications → Message the Librarian: help for family members**

- **Sign-in instructions** and an optional **help link:** how a person gets a
  messaging account and signs in. This is the place for anything specific to your
  server, such as which single sign-on button to choose and who to ask for access.
  Family Librarian does not create messaging accounts or grant access to them.
- **Messaging server name:** the part after the colon in members' IDs. Blank uses
  the bot ID's. Members then type only their username. This is not the homeserver
  URL above, which only has to work from the container and is never shown.
- **Web app address:** an optional browser client to offer.
- **Walk people through installing Element X:** on by default. It adds app store
  buttons and, on wider screens, a QR code. The code and **Open Element** button
  use Element's `mobile.element.io` link, which sends your server name to that
  site. Turn it off if your server cannot be used with Element X, which needs
  Simplified Sliding Sync.

Before inviting the family, walk through the guide once yourself with a new test
account on a real phone.

## Chat assistant

Chat lets signed-in members ask for books in plain language, on the web or by
messaging the Matrix bot. It is off on a new installation.

An administrator opens **Settings → Chat assistant** (`/settings/chat`) and turns
on **Web chat**, **Matrix chat**, or both. The change applies to the next message
without a restart, and **Chat** appears in the menu while web chat is on.

- Matrix chat also needs the Matrix bot connection below, and each member needs to
  link their own Matrix ID.
- Chat works without AI. Setting up AI models does not turn chat on.

For automated lab and test installations only, `Conversation__Enabled=true` with
`Conversation__WebEnabled=true` or `Conversation__MatrixEnabled=true` presets the
switches. These apply only until a choice is saved on the settings page.

## Matrix messaging (Message the Librarian)

Members see this feature as **Message the Librarian**; administrator pages call it
Matrix. Matrix is an optional second notification channel beside SMTP. Every notification
Family Librarian sends reaches a member through each channel they have set up.
Nothing here is required; local sign-in and SMTP keep working without it.

### Administrator setup

1. Create a dedicated Matrix account for Family Librarian on your homeserver
   (not a person's own account) and generate an access token for it, for example
   in Element under **Settings → Help & About → Advanced**.
2. Open **Settings → Communications** and fill in the **Matrix bot connection**
   card: the homeserver URL (for example `https://matrix.example.org`), the bot's Matrix ID,
   and the access token. The token is encrypted at rest like the SMTP password and
   is never shown again; leave the field blank to keep the stored one.
3. Choose **Save settings**, then turn the card's switch on. If the saved
   settings have not passed a connection test, turning the switch on runs one
   first; it only signs the bot in and does not message anyone. If it fails, the
   switch stays off and shows why. **Test Matrix connection** checks the values on
   screen at any time.
4. To let people chat with the bot, also turn on **Matrix chat** under
   **Settings → Chat assistant**. This is a separate switch.
5. Fill in **Message the Librarian: help for family members** on the same page; see
   [First-time setup](#first-time-setup-for-family-members).

The bot account must be able to open direct-message rooms with your members and
receive replies, so do not restrict it to a closed room list. Use a homeserver
reachable from the Family Librarian container.

### Member setup and use

Each member opens **Settings → Message the Librarian** (`/settings/matrix`), or
reaches the same steps in the setup guide. They sign in to their messaging app
first, since a new account may not exist until then, choose **I've signed in**,
enter their username (a full ID also works), and choose **Send my code**. The bot
starts a conversation and sends a one-time code. Accepting that invitation and
replying with the code completes the connection, and the page updates by itself.
Each new code comes in a new conversation, so use the newest invitation. The bot
only reads conversations it started, and cannot read encrypted messages. A member
can send **5 codes in any 15 minutes**; after that the page says the limit is
reached and how long to wait, and the newest code already received still works.
Only sends that reach the homeserver count, and a restart clears the count.
**Disconnect messaging** removes the connection.

Once linked, notifications arrive as chat messages. A "did your book arrive?"
question after a Kindle send can be answered by replying `yes` or `no` in chat
instead of the web page; answering twice is harmless. Quiet hours, which hold
these messages overnight, are described under
[Reverse proxy and secrets](#reverse-proxy-and-secrets). Administrator human-check
alerts also use this channel.

### Troubleshooting

- **Test fails:** check the homeserver URL (include `https://`), that the Bot
  Matrix ID matches the token's account, and that the token has not been logged out.
- **No verification code arrives:** the member must accept the bot's invitation;
  confirm the username, and the messaging server name in the help card. A new
  account must have signed in at least once.
- **The code gets no answer:** the reply must go in the conversation the bot
  started (the newest one), not in a conversation the member started. If the
  conversation is encrypted the bot says it cannot read it; if your server
  encrypts new direct messages by default, turn that off for the bot's rooms.
- **Replies are ignored:** replies are matched from the bot's sync loop; confirm the
  Matrix bot connection is still enabled and the application log shows no sync errors.

## Hardcover metadata provider

Hardcover is an optional book-information source that adds series, author, and
edition details. It does not download files.

Hardcover has no application-level API key, so Family Librarian uses an
**administrator's own personal access token** for household lookups. Individual
members do not need Hardcover accounts.

1. Sign in to Hardcover and create a token on its API settings page.
2. Grant only the `read:catalog:search` and `read:catalog:data` scopes. Do not
   grant `all`; that gives full account access this integration never uses.
3. In Family Librarian open **Metadata providers**, paste the token into the
   Hardcover entry, save, and enable it. The token is encrypted at rest and
   included in database backups.
4. Use **Test connection** to confirm it works.

Hardcover's free tier allows about 5,000 requests per day. Family Librarian
caches lookups and backs off when Hardcover returns a rate-limit response, so a
busy day degrades to the other metadata providers rather than failing searches.
Placeholder or compilation entries in Hardcover's community catalog are filtered
out. Series memberships and completion status feed the **Following** page.

You can disable Hardcover at boot with `MetadataProviders__Hardcover__Enabled=false`.

## Targeted Kindle delivery

Family Librarian uses CWA to send a specific library book to the opted-in
recipient. CWA library availability, submission to CWA, and arrival on a
Kindle are separate facts. CWA's SMTP settings send books; Family Librarian's
Communications SMTP settings serve a different purpose.

### Administrator setup

1. Configure CWA's mail server under its administrator email settings, including
   the provider's required encryption and authentication. Check CWA's email test
   and task/log output. Keep CWA **Auto-Send disabled for every relevant user**:
   its automatic distribution on import would bypass Family Librarian's per-user
   intent and attempt history. See the upstream [CWA email and Auto-Send
   guide](https://github.com/crocodilestick/Calibre-Web-Automated/wiki/Auto-Send-System).
2. Create a dedicated CWA login for Family Librarian. It does not need admin
   privileges. Grant **Allow Downloads**, access to the intended books, and
   **Allow additional eReader email addresses** so the request's recipient can
   differ from the service account's own saved address. These checks are
   enforced by CWA's [targeted-send route](https://github.com/crocodilestick/Calibre-Web-Automated/blob/main/cps/web.py).
   Labels vary by installed CWA release; verify those permissions after an upgrade.
3. In Family Librarian **Publishing settings**, expand CWA and configure its
   base URL in the OPDS section, then save the dedicated **E-reader delivery
   service account** username/password. Catalog OPDS credentials are separate.
   CWA ingest configuration is also separate from sending an existing book.
   The e-reader account requires a reachable form-login endpoint; an interactive
   SSO-only login is not sufficient for this integration.
   Until the base URL, username and password are all saved, **Ebook delivery**
   says Kindle delivery isn't available: users can't add a Kindle address or
   run the sign-in test, and book pages offer no Kindle sends. An address saved
   earlier is kept and can still be edited or disabled.
4. On the same page, fill in **Kindle setup help for family members**; see
   [First-time setup](#first-time-setup-for-family-members).
5. Use **Ebook delivery → Test service sign-in** to verify saved credentials.
   This does not verify send permissions, mail delivery, or on-device arrival.
   Make the first actual send with one intended recipient and one suitable book,
   then confirm it on the device before broad use.

### Recipient setup and use

The setup guide and **Ebook delivery** walk members through this, using the sender
address and Amazon store from **Kindle setup help**. Find the device's
Send-to-Kindle address in Amazon's Content and Devices / Personal Document
Settings. Add the **CWA outgoing sender address** to Amazon's approved
personal-document senders. Save the Kindle address in Family Librarian **Ebook
delivery**, enable it, and choose the default request preference. Family Librarian
cannot check that Amazon accepted the sender; only an arriving book shows that.
Amazon
lists EPUB among supported document formats; an unsupported or protected book
may still fail. Follow [Amazon's sending instructions](https://digprjsurvey.amazon.co.uk/csad/help/node/G5WYD9SAF7PGXRNA)
and [error guidance](https://digprjsurvey.amazon.co.uk/csad/help/node/T48rsVm3gY7KeGkKUk).

Choose Kindle delivery when requesting an ebook, or use **Send to Kindle** on
an existing library book. Open **My Kindle deliveries** to inspect all attempts,
including sends without an acquisition request. Answer **Received** or **Not
received** after a submitted send. Reporting missing does not itself resend.
A notification opens that specific delivery; superseded attempts link to the
latest one. Repeated retry commands on an old attempt cannot create another send.

### Troubleshooting and support

| Displayed state | Meaning and next step |
| --- | --- |
| Waiting to send / Sending | Durable work exists. The worker recovers pending work at startup and every five minutes. |
| Delivery failed | Read the reason. Login/configuration problems require correction; safely retryable failures show their earliest retry time or exhaustion. Recipient eligibility is rechecked before dispatch. |
| Sent — receipt unconfirmed | CWA acknowledged the submission. Check CWA's mail task/logs, Amazon rejection/verification messages, the approved sender and recipient address, and the device's connectivity/sync. This is not proof of arrival. |
| Sent, but not received | The recipient reported missing. The latest attempt offers Retry to the owner and administrator after investigating the cause. |
| Received | The recipient confirmed arrival; no retry is offered. |
| Send outcome unknown | Submission may have succeeded. Check receipt and CWA activity before selecting **Resend (may create a duplicate)**. This state never automatically retries. |
| Cancelled / Superseded | No send will be resumed from that row. Inspect the latest attempt and current target/account/participation settings. |

In **Publishing activity → Ebooks → Kindle**, administrators see the stored book
title (or library reference for older untitled rows), recipient identity, attempt
number, submission and receipt states, receipt time, supersession and automatic
retry timing/exhaustion. Only the latest eligible attempt offers Retry. Open
pages refresh on committed delivery changes and reconnect; one participant's
receipt does not broadcast to other participants of the shared request.

Live health checks update publishing status without replacing unsaved form
values. Save explicitly to apply a draft; reload the page to load configuration
changes made by another administrator.

Existing-copy matching is not a suitability guarantee. The current path does
not establish edition, language, DRM or conversion suitability. Resolve those
requirements before reusing a questionable copy; sending or receiving a test
book does not establish that every matched copy is suitable.

### Kindle delivery upgrade and interrupted sends

`HardenKindleDelivery` preserves existing delivery rows and IDs, assigns a
stable delivery identity, and normalizes attempt numbers before enforcing
unique initial request/user deliveries and unique attempt numbers. Legacy
standalone history is grouped per user, target, provider and external book;
new standalone sends have independent delivery identities.

Earlier retryable failures could represent accepted sends whose acknowledgement
was lost. The migration changes those rows to `SubmissionUnknown` and stops
automatic resends. After upgrading, review the **Ebooks → Kindle** queue in
Admin → Library publishing. For an unknown submission, check whether the book
arrived before choosing **Resend (may create a duplicate)**. Known configuration
failures retain the ordinary Retry action after the configuration is corrected.

The worker runs at startup and every five minutes. It releases saved eligible
intent for already verified ebooks and resumes Pending attempts. Superseded
legacy Pending rows are cancelled instead of dispatched. Submitting
attempts older than five minutes become unknown and require review; restarting
the host never automatically repeats those uncertain sends. A missing receipt
confirmation notification alone is not evidence that delivery failed.

## Back up PostgreSQL

Use the versioned full-backup tooling in `scripts/backups/` for every normal
backup. It creates an atomic, permission-restricted backup set containing the
PostgreSQL custom-format dump, selected CWA/Audiobookshelf configuration and
library archives, a versioned manifest, and SHA-256 checksums. The PostgreSQL
password stays inside the Compose container rather than appearing in shell
history.

Copy `scripts/backups/backup.env.example` to a directory outside the repository
that only the backup operator can read. Configure each installed permanent
library as `directories` and supply the host-visible configuration and library
paths; set a library to `disabled` only when that integration is not installed.
The secret recovery reference is a runbook/secret-manager identifier, never a
raw certificate, password, or access token.

```bash
install -d -m 700 /etc/family-librarian
cp scripts/backups/backup.env.example /etc/family-librarian/backup.env
chmod 600 /etc/family-librarian/backup.env
# Edit the copied file, then create and verify a backup set:
scripts/backups/create-backup.sh --config /etc/family-librarian/backup.env
scripts/backups/verify-backup.sh --backup /var/backups/family-librarian/family-librarian-YYYYMMDDTHHMMSSZ
```

The scripts intentionally run on the deployment host rather than inside the web
container. A web-container scheduler would need Docker-daemon access to run
`pg_dump` and reach arbitrary CWA/Audiobookshelf directories, effectively
granting the browser-facing app host-level control. For a remote library, mount
or otherwise expose the directories through a trusted host path, or use that
library's own backup facility and do not call the resulting set complete until
it is included and verified.

Protect every backup set with the same care as the database: it contains account
emails, request history, provider configuration ciphertext, and the persisted
Data Protection key ring. Store at least one encrypted copy away from the host
running the application, and verify restores periodically.

### Schedule daily or weekly backups

The scheduler is opt-in and host-managed. Generate the cron line for the chosen
frequency, inspect it, then add it to the backup operator's crontab. Daily runs
at 02:15 local server time; weekly runs at 02:15 every Sunday. The script only
prints a line—it never edits a crontab itself.

```bash
scripts/backups/print-cron-entry.sh \
  --frequency daily \
  --repository /srv/family-librarian \
  --config /etc/family-librarian/backup.env \
  --log /var/log/family-librarian-backup.log

# Use --frequency weekly for the weekly alternative.
```

Test a backup manually before enabling a schedule, monitor the generated log,
and arrange off-host replication/encryption separately. Scheduling a backup is
not evidence that it can be restored.

The Compose volume is durable only while the Docker volume exists. `docker
compose down` preserves it; `docker compose down -v` destroys it. Do not use
the latter as an upgrade or troubleshooting step unless a complete local reset
is intended.

## Restore a backup

Restoring replaces PostgreSQL. The restore tooling requires an explicit
`--confirm-replace-postgres`, verifies every checksum first, and refuses to
write CWA/Audiobookshelf data into a non-empty target directory. Stop the
external CWA/Audiobookshelf services before restoring their files; the script
does not control containers outside this Compose project.

```bash
scripts/backups/restore-backup.sh \
  --backup /var/backups/family-librarian/family-librarian-YYYYMMDDTHHMMSSZ \
  --confirm-replace-postgres \
  --cwa-configuration-target /srv/restore/cwa-config \
  --cwa-library-target /srv/restore/cwa-library \
  --audiobookshelf-configuration-target /srv/restore/audiobookshelf-config \
  --audiobookshelf-library-target /srv/restore/audiobookshelf-library
```

Restore into an empty PostgreSQL database (a new volume or a freshly created database). `pg_restore --clean` only drops
objects that are in the dump, so restoring an older backup over a database that already has newer tables fails (for example,
it cannot drop `catalog.works` while the evidence tables, added later, still reference it); the restore then needs a clean
target, after which the migration job brings the schema forward again.

Omit a destination pair only if the backup manifest records that integration as
disabled. The destination directories must be new or empty; after the restore,
attach them to the appropriate external services according to their own
deployment documentation. Use a backup created from the same PostgreSQL major
version or a compatible newer `pg_restore` client. The script starts the
one-shot migration job and Family Librarian, but do not reopen the deployment
until the migration logs and both health endpoints have passed.

## Initial-product full backup and recovery milestone

The backup/restore tooling above implements the repeatable backup-set format,
integrity verification, explicit PostgreSQL replacement, and safe external-file
restore targets. It is not a completed recovery milestone until an operator has
configured it for the real household topology and proved a disposable restore.
Before Family Librarian's initial product is considered complete, the operator
must be able to create and restore a full backup covering:

- PostgreSQL, including the persisted Data Protection key ring and stored
  configuration ciphertext;
- CWA configuration and its complete Calibre library;
- Audiobookshelf configuration and its complete library; and
- the deployment-specific secret and certificate recovery procedure, without
  placing raw secrets in the backup manifest or repository.

The shipped procedure or tooling must identify the component versions and
backup time, preserve integrity information, and be proven by restoring all
components together in a disposable environment. The recovery proof includes
the migration job and health checks, one CWA ownership/OPDS lookup, and one
Audiobookshelf library lookup. A settings-only archive may speed up development
or lab setup, but it is not a substitute for this full recovery capability.

## Reverse proxy and secrets

Place a TLS-terminating reverse proxy in front of port 8080 for an internet
reachable deployment. The application uses cookie authentication, so ensure the
proxy forwards the canonical scheme and host and does not cache API responses.
Use the public HTTPS URL consistently in browser bookmarks and, when optional
OIDC is enabled, in the registered callback and sign-out URIs.

Set `ReverseProxy__TrustedNetworks` to the proxy's own address or network in
CIDR notation. The forwarded-headers middleware only honors `X-Forwarded-For`/
`X-Forwarded-Proto` from an address in that list — unconfigured, only loopback
is trusted, so a proxy running elsewhere (a separate host, a separate
container) is silently ignored rather than trusted by default. Do not point
this at a network wider than the actual proxy: any address inside it can then
spoof its own client IP into request logs and the invitation rate limiter's
per-caller partitioning.

Set `RemoteView__AllowedOrigins__0` (and `__1`, `__2`, ... for more than one)
to this deployment's own public HTTPS origin(s) before enabling any
provider's administrator-brokered remote view (HUMAN-ACQ-1 Phase 3).
WebSocket upgrades are accepted only from an origin in this list —
unconfigured, every upgrade is rejected, never "any origin," to close the
cross-site WebSocket hijacking gap the framework's own default otherwise
leaves open. This mirrors `ReverseProxy__TrustedNetworks` immediately above:
both fail closed until explicitly set for the real deployment topology.

Set `Interaction__PublicOrigin` to this deployment's own public HTTPS origin
(e.g. `https://fl.example.com`, no path/query/fragment) to enable HUMAN-ACQ-1's
Matrix verification alert — the magic link an administrator taps from chat to
open a provider's remote-verification view without signing in first. It must
also already be listed in `RemoteView__AllowedOrigins` above: the magic-link
page opens the same brokered WebSocket, so an origin the WebSocket upgrade
would reject can never actually work, and the application refuses to start if
the two settings disagree. Left unset, the application still starts normally
— it only disables this one alert (a warning is logged at startup) — every
other notification path and the existing in-app "needs you" queue keep
working. `http` is accepted only in the Development environment.

The same origin lets a Matrix chat answer about a book or series already in the
family catalog end with one "Open in Family Librarian" link to that record's
page (`/works/…` or `/series/…`). The link is built only from the catalog's own
id, never from a metadata source, and opening it still requires signing in.
Without `Interaction__PublicOrigin`, chat answers in Matrix carry no links; the
web chat always links saved records.

Each administrator connects their own account at **Settings → Message the
Librarian** (`/settings/matrix`) to receive this alert; an administrator with no
verified connection simply receives none.

Any user can set quiet hours (a time zone and daily window) at **Settings →
Quiet hours** (`/settings/quiet-hours`). Their queued email and Matrix
notifications, and an administrator's verification alerts, wait until the window
ends instead of being dropped. Books are still found and delivered, including
Kindle sends, and in-app notifications are unaffected. Messages a user is waiting
for, such as a messaging code or a chat reply, are not held. A
typed Matrix "yes", "no", "take" or "keep looking" only answers a message that
has already been sent. The page is disabled until email or Matrix delivery is
switched on.

**My Kindle deliveries** appears in the side menu once Kindle delivery is set up,
or for a user who already has delivery history. **Settings → Format preferences**
(`/settings/formats`) sets which formats a user's request forms start with
(ebook, audiobook or both) and their audiobook narration choice; choices for a
format that isn't set up are disabled. Chat requests still state their format.

Until an administrator saves a homeserver and access token and enables Matrix
under **Communications**, that page says messaging isn't available and nobody
can start a connection. An existing one is kept and can still be removed.

Keep PostgreSQL credentials, bootstrap credentials, OIDC secrets, and provider
credentials outside the repository. Provider credentials entered through the
administrator UI are encrypted using the persisted Data Protection key ring in
PostgreSQL; that is why a database backup is required for both credential and
session continuity after recovery.

Set `DataProtection__KeyEncryptionCertificate__Path` (and `__Password` if the
PFX is protected) to encrypt that key ring at rest. Without it, the key ring
itself is stored in PostgreSQL unencrypted — meaning the credentials it
protects and the key that unlocks them live in the same backup, which makes
the encryption decorative against anyone who obtains one. The application logs
a warning at startup when this is unset rather than refusing to start, since a
first deployment has no certificate to provide yet; treat that warning as a
todo, not routine output, once real provider credentials are in use.

## Settings-only archive

An administrator can create an encrypted settings-only archive from **Settings
backup** in the application. It contains current integration, provider, OIDC,
acquisition-policy and family setup-help configuration, including Data Protection
ciphertext for the supported credentials. **Excluded:** CWA e-reader service-account
username/password, the Matrix bot connection, personal Kindle targets, members'
setup progress and all delivery/receipt history.
After import, re-enter the service credentials in Publishing settings and have
users set up their Kindle addresses again. The service sign-in test only checks
login; verify an intended book send separately. It does not contain accounts,
catalogue data, requests, audit history, notifications, jobs, files, or the
Data Protection key ring. It is not a replacement for the PostgreSQL backup
and restore procedure above. Older settings archives may include the removed
Family Librarian private-egress gateway section. Import ignores that section;
external providers manage their own Internet routes. The schema migration drops
the old FL gateway settings and per-provider policy columns.

Import is intentionally create-only: use it only to seed a fresh instance with
none of those settings configured. Before import, the target validates that all
credential ciphertext can be opened by its Data Protection key ring. An archive
with secrets can therefore be imported only when the target has the matching
key ring; copying only the key-encryption certificate is not sufficient. Keep
the archive and passphrase separate, restrict access to both, and use the full
PostgreSQL backup for disaster recovery or a complete migration.
