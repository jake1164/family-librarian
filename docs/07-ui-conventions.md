# UI conventions

Curated conventions for the Blazor client (`FamilyLibrarian.Web.Client`). Keep this
file current when a convention changes; it is the reference `AGENTS.md` points
agents to before they add or edit a chip, status indicator, or similar
small recurring UI element.

## Status color vs. media type

A recurring shape in this app is "one Ebook or Audiobook format, in one
status" — a book request, a provider lookup, a stored file. Two independent
facts are always in play: **which media type** (Ebook vs. Audiobook) and
**what status** it's in. They must never be drawn with the same visual
channel, or a viewer cannot tell "which format is this" from "does this need
my attention" at a glance.

The rule:

- **Chip color always means status**, and only status, everywhere in the app:
  - grey / default — inactive (Cancelled)
  - blue (`Color.Info`) — waiting / in progress, no attention needed
  - amber (`Color.Warning`) — needs attention (NeedsReview, AwaitingApproval,
    SecurityReviewRequired, IdentityReviewRequired, SubmissionUnknown, ...)
  - green (`Color.Success`) — available / done
  - red (`Color.Error`) — failed / not available (SecurityCheckFailed,
    PublishingNeedsAttention, NotAvailable, ...)
- **Media type is conveyed by icon**, never by color: a book icon for Ebook,
  headphones for Audiobook. Pair the icon with a `MudTooltip` naming the media
  type so the distinction isn't icon-only for screen readers.

Security scan/storage statuses use the same `MediaTypeVisuals` mapping and
`RequestStatusChip`: scanning/waiting is blue, interrupted/review-required is
amber, passed/trusted is green, and failed/deleted is red.

This mapping lives in one place —
[`Theme/MediaTypeVisuals.cs`](../src/FamilyLibrarian.Web.Client/Theme/MediaTypeVisuals.cs)
— and nowhere else. Do not re-derive a `status switch` that maps to `Color` in
a page's `@code` block; call into `MediaTypeVisuals` (directly, or through one
of the shared components below) instead. If a new status value is added to
`RequestStatus`, `RequestFormatStatus`, or a progress code, update
`MediaTypeVisuals` once and every page picks it up.

Kindle chips use `Delivery/KindleStatusChip.razor`, backed by
`MediaTypeVisuals.KindleLabel` and `MediaTypeVisuals.KindleColor`: Pending/Submitting and
Submitted without receipt confirmation are blue; confirmed receipt is green;
failed or reported missing is red; `SubmissionUnknown` is amber; cancelled is
neutral. Unknown submissions show **Resend (may create a duplicate)** so the
explicit action communicates its consequence. Do not label an acknowledged
submission as confirmed receipt.

## Availability badges are a separate vocabulary from status

A search-result availability badge (`AvailabilityBadges` in
[`FamilyLibrarian.Web.Client/Catalog/`](../src/FamilyLibrarian.Web.Client/Catalog/))
shows whether a raw catalog candidate — not yet a request, not yet a Work —
was found in CWA, on Project Gutenberg, in Audiobookshelf, or via a custom
provider. Its color is driven by `FulfillmentOptionResponse.OptionKind`
(`Owned`, `DirectAcquisition`, `Availability`, `StoreOffer`, `ExternalAction`),
**not** by the request-status vocabulary above — a search candidate has no
request lifecycle yet. This mapping has its own functions on
`MediaTypeVisuals`, `OptionKindColor`/`OptionKindLabel`, kept deliberately
separate from `StatusColor`/`StatusLabel` rather than folded into that
switch. Media-type icon still comes from the shared `MediaTypeVisuals.Icon`.

## Use the shared components, not a hand-rolled `MudChip`

Three components in
[`FamilyLibrarian.Web.Client/Requests/`](../src/FamilyLibrarian.Web.Client/Requests/)
cover every case:

| Component | Use for | Shows |
| --- | --- | --- |
| `FormatStatusChip` | One request format (Ebook/Audiobook + its status) | icon (media type) + chip colored by status + tooltip; clickable once a safe `ExternalActionUri` is set |
| `RequestStatusChip` | A whole request's status (no single media type) | chip colored by status, short label by default |
| `MediaTypeChip` | A media type with no status attached (e.g. a provider lookup) | neutral/outlined chip + icon + tooltip |

```razor
@* One request's format list *@
@foreach (var format in request.Formats)
{
    <FormatStatusChip MediaType="@format.MediaType" Status="@format.Status"
                       ProgressCode="@format.ProgressCode"
                       ProgressDescription="@format.ProgressDescription"
                       ExternalActionUri="@format.ExternalActionUri" />
}

@* The request's overall status *@
<RequestStatusChip Status="@request.Status" />
```

`ExternalActionUri` is reserved for a safe, ordinary external-library action
such as opening an already-owned copy. It must never carry an external
provider's interaction/control URL, signed URL, bearer capability, or remote
browser destination. Those remain server-side and, when needed, are exposed
only through a separately authorized administrator broker.

`RequestStatusChip` defaults to a short, scannable label
(`MediaTypeVisuals.StatusLabel`, e.g. "Needs review"). Pass `Label="@request.StatusDescription"`
only where a full sentence is the right register — the family-facing pages
(`MyRequests`, `WorkDetail`, `BookDetail`) address the person who made the
request directly ("A librarian is reviewing this request."), which reads fine
there. On admin surfaces (`Tasks`, `RequestQueue`, `RequestDetail`) the viewer
*is* the librarian, so the same sentence reads as narration — use the short
label instead (the default).

For administrator request surfaces, a pending format may override the shared
chip label with a server-reported active acquisition stage (such as
"Downloading and preparing files via LibriVox" or "Processing files") and otherwise say
"Awaiting next check." The underlying `Requested` status and its blue color
remain unchanged. The active stage is transient host activity, not a durable
request transition or evidence that a queued provider has already been asked.
Only the administrator endpoint exposes provider identity and current work.
When saved review candidates identify a particular request format, its admin
chip says "Source review needed" and uses the existing amber
`AwaitingApproval` progress color. Other formats on the same request retain
their own status and color.

## Review-decision language

A review category is routing metadata, not an explanation a person can act
on. Requester and administrator review panels must render the host-provided
plain-language review reason. In particular, an external search whose title
could not be corroborated as the requested Work must say so; it must never be
headed or described merely as a “preference review.” Do not manufacture a
quality recommendation from byte count, publication year, provider ordering,
or free-form quality tags. When there is no meaningful requester-visible
difference between multiple records, tell the requester that a librarian will
compare them and expose the provider/source inspection evidence only in the
administrator panel. Do not collapse source records before persistence: the
administrator must see every record, its human-facing source and record
identifier, and the neutral media facts needed to compare it.
For a legacy review saved before those records were retained, suppress any
one-row requester action rather than presenting a false choice; the admin view
may show a stable built-in-catalogue record page when the stored record ID is
validated.
Where a built-in source has a narrow deterministic tie-breaker, show its
stored decision evidence (the metric, threshold, and runner-up) rather than
calling the result “better.” Do not turn that source-specific evidence into a
general-purpose quality badge or color.

Audiobook format priority is a deterministic acquisition rule, not a status or
quality signal. Do not display M4B/MP3/etc. in status colors or describe the
selected container as “better”; surface the format as ordinary media evidence
when explaining an automatic decision or a genuine same-format tie.

Audiobook narration (Human/Synthetic/Unknown) is the requester's own stated
preference applied to meaningful evidence, not a quality score either. Show it
by kind (e.g. “Human narration — Stewart Wills”, “Computer-generated
narration”) as ordinary media evidence alongside format/size/parts, never in
status colors, and never phrased as one recording being generally “better”
than another. An `Unknown` classification is a valid, expected result — do not
imply it as a defect or guess a kind the provider's own evidence did not state.

## Adding a new status or media type

1. Add the color/label mapping to `MediaTypeVisuals` — not to the page.
2. If it's a new media type beyond Ebook/Audiobook, add its icon to
   `MediaTypeVisuals.Icon` first; every chip that renders a `MediaType` string
   picks it up automatically.
3. Update this file's status-color list above if the new status doesn't fit
   an existing bucket.
