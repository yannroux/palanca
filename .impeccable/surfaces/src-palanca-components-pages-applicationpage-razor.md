---
version: 1
slug: "src-palanca-components-pages-applicationpage-razor"
primary_target: "src/Palanca/Components/Pages/ApplicationPage.razor"
related_targets: ["src/Palanca/Components/Layout/MainLayout.razor","src/Palanca/wwwroot/css/palanca.css"]
---

# Surface brief: Palanca dashboard

## Scope and mode

- **Scope:** every owner-facing page of the dashboard:
  - sign-in, enrollment and passkeys;
  - home (applications);
  - the application page (the flag grid), flag creation, and applications and environments;
  - SDK keys and history;
  - management tokens, which arrive in Phase 2.
- **First surface:** the application page. The other pages inherit its world.
- **Mode:** Operate.

## Task, states, constraints

- **Task:** the routine rollout. Turn a flag on in staging, check it, then turn it on in production. Product truth lives in PRODUCT.md.
- **States the build owes:**
  - Recent changes stand out; "recent" proposed as 24 hours. This was the owner's chosen emphasis.
  - Changes by management tokens are labelled with the token name.
  - Protected environments always confirm.
  - Archived flags appear greyed, with restore.
  - Empty states: no applications yet, no flags yet, and a filter with no match.
  - Secrets are shown once, at creation.
  - Sign-in failure and rate-limit messages.
- **Rejected looks:** playful (bouncy motion, mascots, celebrations) and hacker console (terminal styling, monospace everywhere, neon on black).
- **Constraints:**
  - Static server-side rendering and plain form posts.
  - CSP forbids inline styles and scripts, so every state is a class the server renders, never a `style` attribute.
  - Motion is CSS, because the planned dashboard runs no script except the passkey one.
  - Fonts are self-hosted.
  - Pages must be usable at phone width.

## Unresolved

- **Protected confirmation:** an HTML `popover` that opens without script, with a server-side confirmation page as the fallback when a protected toggle arrives unconfirmed.
- **Phone layout of the grid:** proposed as one row per flag, with that flag's environment tiles beneath its key.
- **Showing the owner as actor:** the PRD records the owner's email as the actor of dashboard changes. Open: whether the grid shows it in full or shortened.

## Direction contract

THESIS: The flag grid is a departure board: every value is a split-flap tile, and the latest changes are the ones that just flipped. It refuses the settings table with pill switches.

OWN-WORLD:
- Concourse-grey ground.
- Charcoal board for the header and the Latest flips board.
- Flap tiles with a hinge line behind their letters.
- Signal yellow for ON, recent flips and the active page.
- Red only for protected environments.
- Barlow Condensed capitals for board type and keys; Barlow for running text.
- Six disciplines kept from the declined challengers:
  - flat colour, no shadows or gradients;
  - one module sizes tiles, rows and columns;
  - hierarchy by type scale and weight alone;
  - archive, revoke and protected confirmation set apart from routine actions;
  - archived flags greyed at the end;
  - rest, hover, focus, pressed and disabled marks on every control.

STORY: Before reading anything, the owner sees which flags are on where and what flipped recently. They turn a flag on in staging with one click and watch the tile flip. Later they turn it on in production through a confirmation that names the application, environment, flag and new value.

FIRST VIEWPORT:
- **Header:** a 64px charcoal header. PALANCA set as seven flap tiles; navigation (Applications, History, Passkeys) with a yellow underline on the active page; the application switcher and Sign out on the right.
- **Title row:** on the concourse ground, the application name in 40px condensed capitals, then the flag count and environment revisions. The filter and New flag sit on the right.
- **Main column:** a white table with flags as rows and environments as columns in their sort order. Each cell holds an ON/OFF tile plus when and who. The protected column is headed by a red PROTECTED plate.
- **Right column:** a 360px charcoal Latest flips board.
- **Primary action:** the tile itself.

SIGNATURE: After the post and redirect, the toggled tile plays one split-flap flip (about 300 ms, top half folding down). Recent tiles keep a yellow ring. Under prefers-reduced-motion there is no flip.

FORM: Split-flap departure board; candidate 4 of 7 on the grounded list, assigned by the roll; seed 694dbb8f. Chosen sketch: .impeccable/mocks/decision/assigned.html.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
