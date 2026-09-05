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
    SecurityReviewRequired, IdentityReviewRequired, ...)
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

## Use the shared components, not a hand-rolled `MudChip`

Three components in
[`FamilyLibrarian.Web.Client/Requests/`](../src/FamilyLibrarian.Web.Client/Requests/)
cover every case:

| Component | Use for | Shows |
| --- | --- | --- |
| `FormatStatusChip` | One request format (Ebook/Audiobook + its status) | icon (media type) + chip colored by status + tooltip |
| `RequestStatusChip` | A whole request's status (no single media type) | chip colored by status, short label by default |
| `MediaTypeChip` | A media type with no status attached (e.g. a provider lookup) | neutral/outlined chip + icon + tooltip |

```razor
@* One request's format list *@
@foreach (var format in request.Formats)
{
    <FormatStatusChip MediaType="@format.MediaType" Status="@format.Status"
                       ProgressCode="@format.ProgressCode"
                       ProgressDescription="@format.ProgressDescription" />
}

@* The request's overall status *@
<RequestStatusChip Status="@request.Status" />
```

`RequestStatusChip` defaults to a short, scannable label
(`MediaTypeVisuals.StatusLabel`, e.g. "Needs review"). Pass `Label="@request.StatusDescription"`
only where a full sentence is the right register — the family-facing pages
(`MyRequests`, `WorkDetail`, `BookDetail`) address the person who made the
request directly ("A librarian is reviewing this request."), which reads fine
there. On admin surfaces (`Tasks`, `RequestQueue`, `RequestDetail`) the viewer
*is* the librarian, so the same sentence reads as narration — use the short
label instead (the default).

## Adding a new status or media type

1. Add the color/label mapping to `MediaTypeVisuals` — not to the page.
2. If it's a new media type beyond Ebook/Audiobook, add its icon to
   `MediaTypeVisuals.Icon` first; every chip that renders a `MediaType` string
   picks it up automatically.
3. Update this file's status-color list above if the new status doesn't fit
   an existing bucket.
