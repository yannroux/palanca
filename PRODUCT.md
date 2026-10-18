# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

- **The owner** is the only person who signs in to the dashboard. They develop and run HogarHero and will register later applications.
- **Main situation: a routine rollout.** The owner turns a flag on in staging, checks the result, then turns it on in production. This happens a few times a week, calmly, at a desktop. When priorities conflict, the dashboard serves this situation first.
- **Other jobs:**
  - turning off a misbehaving feature in production;
  - reviewing what they or an AI agent changed;
  - registering an application with its environments and SDK keys.
- **Phones:** the owner uses a phone only occasionally. Every page must be readable and usable at phone width, but the design targets desktop.
- **AI agents and scripts** never use the dashboard. They change flags through the management API with a scoped token. Their changes appear in the dashboard's history, attributed to that token.
- **Application developers** integrate the `Palanca.Client` package in code. They have no dashboard role.

## Product Purpose

Palanca lets the owner turn boolean feature flags on and off per application and per environment at runtime, with no deployment. One self-hosted instance serves every application and environment. HogarHero (staging and production) is the first consumer.

It exists because HogarHero's behavior switches live in deployment configuration today, so changing one means a redeployment. Hosted providers add an external dependency and a recurring cost for a narrow need.

Success means:
- turning a behavior on or off in any environment takes one click and no deployment;
- every change is attributable;
- a consuming application keeps working when Palanca is down.

## Positioning

- **Boolean only, on purpose:** each flag is a switch per environment. There is no targeting, no percentage rollout, no experiments and no remote configuration.
- **Single owner:** one human account with passkey-only sign-in, and no user management.
- **Agents are first-class but bounded:** an agent can create and toggle flags through the API, within the scope of its token, and every change it makes names that token.
- **Small:** one container, SQLite on the host the owner already runs, no external service and no recurring cost.

## Operating Context

- **Hosting:** the HogarHero VPS, behind Caddy and the Cloudflare proxy, at `palanca.hogarhero.com`. Cloudflare only proxies the hostname; no Cloudflare Access.
- **Owner account:** created and recovered only by a command run in the container, which prints a single-use enrollment link valid for 15 minutes.
- **Consuming applications** poll for flag values every 30 seconds by default. A change in the dashboard therefore reaches them within the poll interval plus 5 seconds, not instantly.
- **Protected environments** (production, for example): toggling a flag there asks for a confirmation that names the application, environment, flag and new value.
- **Secrets are shown once:** SDK keys, and management tokens from Phase 2, appear in full only at creation. Afterward only a key's prefix is visible.
- **Change records** are append-only. Each one records its source (dashboard, API or host command) and its actor.

## Capabilities and Constraints

- **Screens in Phase 1:**
  - sign-in (one action: sign in with a passkey);
  - enrollment, reached only from an enrollment link;
  - passkeys (list, add, rename, remove any but the last);
  - home (applications with their environments and flag counts);
  - application page (one grid: flags as rows, environments as columns, a toggle per cell, each flag's key and description, who changed each value last and when, filter by key or description);
  - flag creation (key and description required; optional initial values), plus editing a flag's description, archiving and restoring it, and permanently deleting an archived flag;
  - applications (create, rename, archive) and environments (create, rename, reorder, protect, archive);
  - SDK keys per environment;
  - history (newest first, filterable by application, environment, flag and source).
- **Every page** offers sign-out.
- **Phase 2** adds the management tokens page.
- **Rendering:** static server-side rendering with ASP.NET Core Razor components and plain HTML form posts with antiforgery tokens. There is no interactive render mode and no websocket (FLG-044).
- **Scripts:** the plan for version 1 is one small script that calls the WebAuthn API for passkeys. The PRD also allows Blazor's enhanced navigation (`blazor.web.js`, which opens no live connection) and a small script for the protected-environment confirmation. Neither is decided.
- **Content Security Policy**, as `src/Palanca/Security/SecurityHeadersMiddleware.cs` sends it: `default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'`.
  - No inline scripts, no inline `<style>` elements and no `style` attributes.
  - No `<base>` element, and no `data:` images.
  - No external fonts, styles, scripts or images: fonts are self-hosted.
- **Language:** English only.
- **Terminology:** application, environment, protected environment, flag, flag value (on/off), SDK key, management token, change record, source (dashboard, API, host command), actor, revision, archive, restore.
- **Keys** (application, environment, flag) use lowercase letters, digits and hyphens, start with a letter, are at most 64 characters, and never change. Example: `operator-denue-imports`.
- **Footprint:** the container runs within 256 MB of memory.
- **Scale:** the application page handles 20 to 100 flags comfortably, so scrolling and the filter matter. Version 1 starts with one application, two environments (staging, production) and a handful of flags. The number of later applications is not known.

## Brand Commitments

- **Name:** Palanca, Spanish for "lever". In Mexico, *tener palanca* also means "to have pull".
- **Identity:** Palanca has its own identity, independent of HogarHero. HogarHero is the first consumer, not the parent brand.
- **Assets:** no logo or other brand assets exist yet.

## Evidence on Hand

- **Product requirements:** the PRD, *PRD: Palanca, self-hosted feature flags*, in Outline (last updated 2026-10-17).
- **Real switches:**
  - `DenueProspects__Enabled`, which allows operator-started DENUE imports, is a HogarHero switch that can become a flag.
  - `PREVIEW_MODERATOR_ACCESS_ENABLED` stays configuration: HogarHero reads it at startup to decide which endpoints to map, and the PRD keeps such settings out of Palanca.
- **No dashboard exists yet:** no screenshots, usage data, metrics or testimonials. Do not invent them.

## Product Principles

1. **The calm rollout comes first.** Seeing a flag's state across environments, and moving it from staging to production, is the main path.
2. **Production is never one careless click away.** Protected environments always ask for an explicit confirmation that names exactly what will change.
3. **Every change has an author.** Who or what made it, when, and through which source is always visible, and agents' changes stand out.
4. **Small on purpose.** Boolean flags only. Requests for targeting, rollouts or experiments belong to another product.
5. **Secrets appear once.** Keys and tokens are shown in full only at creation and never again.
