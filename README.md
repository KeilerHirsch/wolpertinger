# WOLPERTINGER

[![CI](https://github.com/KeilerHirsch/wolpertinger/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/KeilerHirsch/wolpertinger/actions/workflows/ci.yml)

**Your copilot should know what to do.**

WOLPERTINGER is a local-first companion for **Elite Dangerous** that brings the right information forward for what you are doing now — without making an external AI provider the owner of your game state.

**Pre-alpha** · no supported end-user release yet.

<p align="center">
  <img src="docs/assets/wolpertinger-concept-ui.webp" alt="WOLPERTINGER concept UI showing route, exploration, engineering, and shipboard-assistant context in an Elite Dangerous cockpit" width="100%">
</p>

<p align="center"><sub><strong>Concept UI.</strong> Product direction, not an implemented-feature screenshot.</sub></p>

## What it does

- **Explore** without digging through panel after panel.
- **Route** with context that follows the current task.
- **Engineer** with the information that matters now.
- **Stay local** for authoritative game context; optional providers add capability, not ownership.

## Today

The trusted foundation is working. The commander-facing tray, overlay and hub experience is still in development.

One authoritative fact stream is designed to feed every surface, so the commander does not become the integration bus.

## Under the hood

- Deterministic local game-state processing.
- A small isolated trusted core for critical boundaries.
- Tests, replay and proof-backed gates for important claims.

**High-assurance inside. KISS outside.**

**Public docs:** [Roadmap](ROADMAP.md) · [Licence](LICENSE) · [Notices](NOTICE.md)

## Support

Funding is voluntary and creates no feature, ranking, roadmap, review, release or technical-influence entitlement.

## Licence

WOLPERTINGER-owned code is licensed under the **European Union Public Licence 1.2 (EUPL-1.2)**. Third-party dependencies and assets retain their own licences and notices.
