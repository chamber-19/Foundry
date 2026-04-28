# AGENTS.md

> **This file is a redirect.** All Copilot, Codex, and coding-agent guidance for this repo lives in:
>
> - **Repo-specific rules:** [.github/copilot-instructions.md](./.github/copilot-instructions.md)
> - **Family-wide rules:** [chamber-19/.github](https://github.com/chamber-19/.github/blob/main/.github/copilot-instructions.md)
>
> Both files load together. Repo-specific rules win on conflict.

Foundry is the internal **agent broker** for the Chamber 19 tool family. It receives GitHub events and Discord commands, routes them to local-LLM agents (via Ollama), and posts structured output back to GitHub PRs / Discord channels.

For a detailed picture of the architecture, see [README.md](./README.md).
