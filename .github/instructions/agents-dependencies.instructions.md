---
applyTo: "src/**,bot/**,evals/**"
---

# Foundry Agent And Dependency Monitoring Instructions

- Dependency monitoring polls GitHub on a schedule and posts Discord alerts
  through the bot. Do not expose localhost broker ingress just for alerts.
- `dep-reviewer` runs deterministic extraction/classification first, uses
  Ollama JSON summaries only as enrichment, and applies rule-based categories:
  `info`, `needs-review`, `risky`, `blocked`.
- Dependabot alerts and Dependabot PRs are GitHub's source of truth.
- Every shipped agent needs a golden eval set before leaving shadow mode.
