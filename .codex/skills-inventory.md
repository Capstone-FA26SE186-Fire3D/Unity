# Installed project skills — Unity

Updated: 2026-09-26

## Unity performance

- `unity-ecs-patterns` — source `wshobson/agents`, path `plugins/game-development/skills/unity-ecs-patterns`, revision `2907e59378b0c0a369d029e1588c1be2397f8bfc`.
  - Vendored under [`.agents/skills/unity-ecs-patterns`](../.agents/skills/unity-ecs-patterns/).
  - Read its `SKILL.md` first; consult `references/details.md` only for DOTS/ECS, Job System or Burst work.
  - The user selected ECS from the start for hazards/NPCs. Use Components/Systems/Jobs within each simulation feature as code is implemented; GameObject/MonoBehaviour remains for player, camera, UI and bridge. Scaffold does not install Entities/Burst; validate APIs against the selected versions and profile Android behavior.

## Project boundary

Unity implementation must follow the package/session/runtime contracts in Docs and coordinate with Mobile's native Android bridge. A skill cannot authorize a different API, direct database access, embedded credentials, or a production-safety claim.
