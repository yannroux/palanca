---
name: Palanca
description: A self-hosted feature-flag dashboard set as a split-flap departure board.
colors:
  ground: "#EDEFEE"
  surface: "#FFFFFF"
  rule: "#D3D6D5"
  field-border: "#8A8F90"
  code-ground: "#DFE2E1"
  disabled: "#DADDDC"
  highlight: "#FAFAF7"
  board: "#1C1E1F"
  flap: "#2A2D2F"
  hinge: "#0B0C0D"
  letter: "#F4F1E8"
  letter-muted: "#B9BCBD"
  letter-off: "#9DA1A3"
  ink: "#1C1E1F"
  muted: "#5E6366"
  placeholder: "#6E7376"
  on: "#F2B705"
  on-hover: "#FFC93D"
  recent: "#7A5800"
  protect: "#C8102E"
  protect-dark: "#A30D25"
  protect-on-board: "#FF6B7A"
typography:
  display:
    fontFamily: "Barlow Condensed, Barlow, system-ui, sans-serif"
    fontSize: "40px"
    fontWeight: 700
    lineHeight: 1
    letterSpacing: "0.04em"
  headline:
    fontFamily: "Barlow Condensed, Barlow, system-ui, sans-serif"
    fontSize: "22px"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "0.06em"
  title:
    fontFamily: "Barlow Condensed, Barlow, system-ui, sans-serif"
    fontSize: "17px"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "0.03em"
  body:
    fontFamily: "Barlow, system-ui, sans-serif"
    fontSize: "15px"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "normal"
  body-small:
    fontFamily: "Barlow, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: "normal"
  label:
    fontFamily: "Barlow Condensed, Barlow, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 600
    lineHeight: 1
    letterSpacing: "0.1em"
  tile:
    fontFamily: "Barlow Condensed, Barlow, system-ui, sans-serif"
    fontSize: "15px"
    fontWeight: 700
    lineHeight: 1
    letterSpacing: "0.14em"
  code:
    fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"
    fontSize: "0.92em"
    fontWeight: 500
    lineHeight: 1.4
rounded:
  sm: "2px"
  md: "3px"
  lg: "4px"
spacing:
  module: "4px"
  xs: "8px"
  sm: "12px"
  md: "16px"
  lg: "24px"
  xl: "32px"
  gutter: "40px"
  section: "48px"
  apart: "64px"
components:
  button-primary:
    backgroundColor: "{colors.board}"
    textColor: "{colors.letter}"
    typography: "{typography.label}"
    rounded: "{rounded.md}"
    padding: "0 18px"
    height: "40px"
  button-primary-hover:
    backgroundColor: "{colors.hinge}"
    textColor: "{colors.on}"
  button-signal:
    backgroundColor: "{colors.on}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "0 18px"
    height: "40px"
  button-signal-hover:
    backgroundColor: "{colors.on-hover}"
    textColor: "{colors.ink}"
  button-quiet:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "0 18px"
    height: "40px"
  button-quiet-hover:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
  button-danger:
    backgroundColor: "transparent"
    textColor: "{colors.protect}"
    rounded: "{rounded.md}"
    padding: "0 18px"
    height: "40px"
  button-danger-hover:
    backgroundColor: "{colors.protect}"
    textColor: "{colors.surface}"
  button-danger-solid:
    backgroundColor: "{colors.protect}"
    textColor: "{colors.surface}"
    rounded: "{rounded.md}"
    padding: "0 18px"
    height: "40px"
  button-danger-solid-hover:
    backgroundColor: "{colors.protect-dark}"
    textColor: "{colors.surface}"
  button-disabled:
    backgroundColor: "{colors.disabled}"
    textColor: "{colors.muted}"
  input:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    typography: "{typography.body}"
    rounded: "{rounded.md}"
    padding: "0 12px"
    height: "40px"
  tile-off:
    backgroundColor: "{colors.board}"
    textColor: "{colors.letter-off}"
    typography: "{typography.tile}"
    rounded: "{rounded.md}"
    width: "60px"
    height: "30px"
  tile-on:
    backgroundColor: "{colors.on}"
    textColor: "{colors.ink}"
    typography: "{typography.tile}"
    rounded: "{rounded.md}"
    width: "60px"
    height: "30px"
  tile-small:
    width: "48px"
    height: "24px"
  wordmark-flap:
    backgroundColor: "{colors.flap}"
    textColor: "{colors.letter}"
    rounded: "{rounded.sm}"
    width: "22px"
    height: "30px"
  plate-protected:
    backgroundColor: "{colors.protect}"
    textColor: "{colors.surface}"
    rounded: "{rounded.sm}"
    padding: "3px 7px"
  board-bar:
    backgroundColor: "{colors.board}"
    textColor: "{colors.letter-muted}"
    padding: "0 40px"
    height: "64px"
  board-panel:
    backgroundColor: "{colors.board}"
    textColor: "{colors.letter}"
    rounded: "{rounded.lg}"
    padding: "20px 20px 8px"
  ledger:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    height: "56px"
  confirm:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.lg}"
    padding: "24px"
    width: "440px"
---

# Design System: Palanca

## Overview

**Creative North Star: "The Departure Board"**

Palanca's dashboard reads like a station's departure board hung in a concourse. The page ground is a pale concourse grey; the shell's header and the side panels are a charcoal board; every flag value is a split-flap tile, charcoal for OFF and signal yellow for ON, with a hairline hinge drawn across its middle. The latest changes appear in a Latest flips panel styled like a departures list, and a tile that has just been toggled plays one split-flap fold. Before reading a word, the owner sees which flags are on where and what changed recently.

The density is that of an operations board: 56px rows, 40px controls, condensed capitals for everything the board "prints" (headings, labels, buttons, tiles, navigation) and a plain humanist sans for running text. Hierarchy comes from type scale and weight, not from colour or depth. Surfaces are flat; the only depth anywhere is the hinge line and the dim behind a confirmation popover.

The world lives under hard constraints that shaped it: static server-side rendering with plain form posts, a Content Security Policy that forbids inline styles and scripts and allows only self-hosted fonts (so asset URLs are root-relative and there is no `<base>`), and a single script (`passkeys.js`). Every interaction is therefore CSS and HTML: disclosures (`<details>`) for menus, the `popover` attribute for confirmations, and a post-redirect query parameter to trigger the flip. The world rejects the settings table with pill switches.

**Key Characteristics:**
- Concourse grey ground, charcoal board for the shell, white ledger tables for data.
- Split-flap tiles as the one primary action: charcoal OFF, yellow ON, hinge behind the letters.
- Barlow Condensed capitals for board type; Barlow for running text; keys always verbatim.
- Flat colour throughout: no shadows, gradients or blur.
- A 4px module: 64px header, 56px rows, 40px controls.
- Yellow for ON and what is current; red for protected environments, destruction and errors.
- One fold animation (320ms), removed under reduced motion.

## Colors

A grey-and-charcoal transit palette with two signal colours, yellow and red, each carrying one meaning.

### Primary
- **Signal Yellow** (on): the ON state of every tile, the active-page underline in the header, the ring around recently changed tiles, board headings on charcoal (gate title, Latest flips), the agent attribution line in Latest flips, the signal button, text selection and focus rings on charcoal.
- **Lit Yellow** (on-hover): hover state of the signal button only.
- **Departed Amber** (recent): the "just now · you" text beside a recent tile and the success notice icon on white; yellow's legible form on light ground.

### Secondary
- **Platform Red** (protect): the PROTECTED plate, protected environment tags, danger buttons, error notice borders and invalid fields.
- **Deep Platform Red** (protect-dark): hover of the solid danger button.
- **Board Red** (protect-on-board): error notices inside a charcoal panel, where Platform Red would be too dark.

### Neutral
- **Concourse Grey** (ground): the page background behind everything.
- **Ticket White** (surface): ledger tables, forms, notices, empty states, confirmation dialogs.
- **Platform Rule** (rule): 1px table and panel borders and row dividers.
- **Field Steel** (field-border): input and quiet-button borders, environment tag outlines.
- **Code Ground** (code-ground): inline code on light surfaces.
- **Disabled Grey** (disabled): disabled buttons and the archived-flags count chip.
- **Row Light** (highlight): hovered application rows and the targeted board row.
- **Board Charcoal** (board): header bar, gate panel, Latest flips panel, switcher menu, OFF tiles, primary buttons.
- **Flap Charcoal** (flap): the wordmark's flaps, tiles inside the Latest flips panel, menu hover, notices on charcoal.
- **Hinge Black** (hinge): the 1px hinge line on flaps and tiles; primary button hover.
- **Board Letter** (letter): primary text on charcoal.
- **Board Letter Muted** (letter-muted): secondary text on charcoal; inactive nav.
- **Board Letter Off** (letter-off): the OFF lettering on a charcoal tile.
- **Ink** (ink): body text on light ground; same value as Board Charcoal.
- **Muted Ink** (muted): meta lines, descriptions, table headings, form labels.
- **Placeholder Ink** (placeholder): field placeholders only.

### Named Rules
**The Yellow Means On Rule.** On light ground, yellow marks only ON, what is current (active page, recent change) and the signal action. It is never decoration.

**The Red Is a Warning Rule.** Red marks protected environments, destructive actions and errors, nothing else. A red element always means "this is consequential".

## Typography

**Display Font:** Barlow Condensed (with Barlow, system-ui, sans-serif), weights 500, 600 and 700
**Body Font:** Barlow (with system-ui, sans-serif), weights 400, 500 and 600
**Label/Mono Font:** ui-monospace stack for inline code only

**Character:** Barlow Condensed in tracked capitals is the board's printed lettering, close to transit signage; Barlow is the same family's plain voice for sentences. Both are self-hosted Latin subsets under the SIL OFL (`src/Palanca/wwwroot/fonts/`).

### Hierarchy
- **Display** (700, 40px, line-height 1, 0.04em, uppercase; 32px at phone width and in the gate panel): page titles. A flag page's title is the flag key and drops the uppercase.
- **Headline** (700, 22px, line-height 1.1, 0.06em, uppercase): section headings, empty-state titles, application names on the home list, confirmation titles. The Latest flips heading is a smaller, wider variant (18px, 0.14em).
- **Title** (600, 17px, line-height 1.2, 0.03em, case preserved): flag keys in the board and names in ledgers. Sub-headings in forms use 16px, 0.08em, uppercase.
- **Body** (400, 15px, line-height 1.5): running text, inputs, meta lines. Prose blocks cap at 65ch, notices at 64ch, flag descriptions at 60ch.
- **Body small** (400, 12-14px): descriptions (13px), "when and who" beside tiles (12px), hints (13px), secondary meta (14px).
- **Label** (600, 13-15px, line-height 1, 0.1em to 0.12em, uppercase): form labels and legends (14px), buttons and navigation (15px), table headings (14px, 0.12em), definition terms (13px).
- **Tile** (700, 15px, 0.14em, uppercase; 13px on small tiles): ON/OFF lettering. Plates use 11px at 0.14em.

### Named Rules
**The Keys Verbatim Rule.** Board type is set in capitals, but application, environment and flag keys always appear exactly as code reads them: case preserved, tracking reduced to 0.01-0.03em.

**The Scale and Weight Rule.** Hierarchy is carried by size, weight and case. Colour separates only ink from muted ink.

## Layout

Everything sizes from a 4px module: the header is 64px (16 modules), ledger and board rows 56px (14), controls 40px (10). Page padding is 32px 40px 64px on desktop and 24px 16px 48px at phone width; the header keeps 40px side padding (16px on phones). The spacing steps in use are 8, 12, 16, 24, 32, 40, 48 and 64px; form sections start 48px apart and the danger zone 64px below the last section.

The application page is a two-column grid: the board takes the remaining width and Latest flips a fixed 340px column, 40px apart. Ledger tables and the application list cap at 1100px; forms at 640-760px; the gate is a single centred column up to 600px.

Below 1200px, Latest flips drops beneath the board (max 640px wide). At 720px and below the board becomes one block per flag: the header row is hidden, each environment's cell gains its own label (with its PROTECTED plate) beside the tile, ledgers collapse to labelled stacks, the owner's email leaves the header, and page tools take the full width.

The header is not sticky; the board's column headings are, so environment names and the PROTECTED plate stay in view while scrolling through 20-100 flags.

## Elevation & Depth

The system is flat. There are no shadows, gradients, blurs or translucent surfaces. Depth is conveyed by tone (charcoal board against grey concourse against white ticket) and by one physical detail: the 1px hinge line across each flap and tile, drawn behind the letters so it never reads as a strike-through. On a yellow tile the hinge is black at 25% opacity. The only overlay treatment is the confirmation popover's flat backdrop, Board Charcoal at 50%.

### Named Rules
**The Hinge Is the Only Depth Rule.** If a surface needs to stand out, change its tone or its border, never add a shadow.

## Shapes

Corners are nearly square, in three steps: 2px for the smallest printed pieces (wordmark flaps, plates, environment tags, code, count chips), 3px for controls and data containers (buttons, fields, tiles, notices, empty states), 4px for panels (gate, Latest flips, switcher menu, confirmations). Borders are 1px; the one heavy rule is the 2px ink line under table headings. Nothing is pill-shaped or circular.

## Components

### Buttons
Printed, tracked capitals on a flat block; each state has its own visible mark.
- **Shape:** gently squared (3px), 40px high, 18px side padding, 10px gap to an icon.
- **Primary:** Board Charcoal with Board Letter, the default for "New flag" and form submits. Hover turns it Hinge Black with yellow lettering.
- **Signal:** Signal Yellow with ink, for turning a protected value on.
- **Quiet:** transparent with a Field Steel border; hover fills white and darkens the border to ink.
- **Danger:** red outline and red text, filling red on hover; **Danger solid:** red fill for the confirming step, darker red on hover.
- **States:** 120ms ease-out colour transition, 1px press-down on active, Disabled Grey fill with muted text and a not-allowed cursor, a progress cursor while busy. Icon-only buttons are 40px square.
- **Quiet link:** a tracked-capital underlined link (14px) for secondary navigation such as Settings and rename disclosures.

### Chips
- **PROTECTED plate:** solid red, white 11px capitals, 2px corners; heads a protected column and repeats in confirmations. An ARCHIVED variant uses muted ink.
- **Environment tag:** 1px Field Steel outline, 12px capitals; red outline and text when protected.
- **Count chip:** Disabled Grey block with ink figures, beside Archived flags.

### Cards / Containers
- **Corner Style:** 3px (notices, empty states), 4px (panels and dialogs).
- **Background:** Ticket White on the concourse; Board Charcoal for board panels.
- **Shadow Strategy:** none; see Elevation & Depth.
- **Border:** 1px Platform Rule; confirmations use a 1px ink border.
- **Internal Padding:** 12px 16px for notices, 24px for dialogs, 32px for empty states and the gate panel.
- Applications and flags are rows, never cards.

### Inputs / Fields
- **Style:** white, 1px Field Steel border, 3px corners, 40px high, 12px side padding, Barlow 15px; textareas from 96px tall.
- **Hover / Focus:** the border darkens to ink; focus adds the 2px ink outline at 1px offset.
- **Error:** invalid fields take a red border after interaction. Placeholder text uses Placeholder Ink.
- **Labels:** muted tracked capitals above the field, 8px gap. Checkboxes are 18px with charcoal accent colour.

### Navigation
- **Board bar:** the 64px charcoal header on every page: the wordmark, then navigation in 15px tracked capitals (muted, Board Letter on hover), then the application switcher, the owner's email and Sign out on the right.
- **Active page:** Board Letter with a 3px yellow underline flush with the bar's bottom edge.
- **Application switcher:** a `<details>` disclosure on the bar; its menu is a charcoal panel with flap-charcoal hover rows, each showing the name and the key.
- **Focus on charcoal:** focus outlines turn yellow inside the bar and the gate panel.

### Split-Flap Tile (signature)
- A 60 x 30px block (48 x 24px in Latest flips), 3px corners, 15px tracked capitals reading ON or OFF. OFF is charcoal with Board Letter Off; ON is yellow with ink. The hinge line sits behind the letters.
- The tile is the board's primary action: a submit button for ordinary environments, a popover trigger for protected ones. Hover draws a 2px muted outline at 2px offset; focus a 2px ink outline; press moves it down 1px.
- A tile changed recently (and its "when and who" text in Departed Amber) carries a 2px yellow ring at 2px offset.
- **The fold:** after the toggle's redirect, the tile shows the old value's top half as a flap that folds down (320ms, `cubic-bezier(0.55, 0, 0.75, 0.2)`, rotateX 0 to -90deg at 120px perspective). The new letters appear only when the flap has fallen, so the two never overlap. Under `prefers-reduced-motion` there is no fold.

### Wordmark
PALANCA set as seven Flap Charcoal tiles (22 x 30px, 19px capitals, 3px apart; 30 x 40px large; 18 x 26px on phones), each with a hinge. The favicon repeats the idea: a yellow P on a charcoal square split by a black hinge.

### Latest Flips
A charcoal panel listing recent changes like departures: the time in its own 48px column, then the flag key and "environment · who", then a small tile. Changes made by an agent through the API show their attribution line in yellow.

### Confirmation
A `popover` dialog (no script), white with an ink border, up to 440px wide over a flat dimmed page. It names application, environment (with PROTECTED plate), flag and the new value as a tile, then offers the confirming button (signal for on, danger solid for off) beside a quiet Cancel. The same confirmation exists as a standalone page for the no-popover path.

### Notices
A white strip with a 1px rule border, a 20px stroke icon and one line of text: success icon in Departed Amber, error with a red border and icon. Inside charcoal panels notices turn Flap Charcoal, with a yellow success icon and Board Red errors.

### Icons
Drawn inline SVG on a 20px grid, 1.75 stroke, round caps and joins, `currentColor`: check, alert, info, key, sign-out, chevrons. Decorative unless labelled.

## Do's and Don'ts

### Do:
- **Do** show every flag value as a split-flap tile: charcoal OFF, yellow ON, hinge behind the letters.
- **Do** size rows, controls and the header from the 4px module (56px rows, 40px controls, 64px header).
- **Do** set headings, labels, buttons and navigation in tracked Barlow Condensed capitals, and running text in Barlow.
- **Do** show keys exactly as code reads them, with case preserved.
- **Do** keep archive, revoke, delete and protected confirmation apart from routine actions: spaced away from routine buttons in rows (32px in ledgers, an extra 16px in settings), in a danger zone 64px below the last section, behind a confirmation that names what changes.
- **Do** keep archived items greyed at the end of their list.
- **Do** give every control a distinct rest, hover, focus, pressed and disabled mark.
- **Do** build interactions from HTML and CSS (`<details>`, `popover`, form posts); keep all styles in the stylesheet and all assets self-hosted with root-relative URLs.
- **Do** drop the fold and button transitions under `prefers-reduced-motion`.

### Don't:
- **Don't** use pill switches for flag values; on the board and in Latest flips the tile is the toggle and the readout.
- **Don't** add shadows, gradients, blur or translucent surfaces; depth is tone and the hinge.
- **Don't** use yellow for decoration on light ground, or red for anything but protected, destructive or error states.
- **Don't** present applications or flags as cards; they are rows.
- **Don't** use inline `style` attributes, inline scripts, external fonts or a `<base>` element; the Content Security Policy forbids them.
- **Don't** round anything beyond 4px.
