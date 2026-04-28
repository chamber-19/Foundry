# Contributing to Foundry

Foundry is the internal agent broker for the [chamber-19](https://github.com/chamber-19) tool family.
It is not a public-facing service, but contributions from chamber-19 collaborators are welcome.

## Read first

- **Org-wide rules:** [chamber-19/.github copilot-instructions.md](https://github.com/chamber-19/.github/blob/main/.github/copilot-instructions.md)
- **Repo-specific rules:** [`.github/copilot-instructions.md`](./.github/copilot-instructions.md)

The repo-specific rules describe the agent design contract, local-LLM constraints, eval requirements,
and what not to rebuild (the old ML pipeline).

## Build commands

### .NET broker

```text
dotnet restore Foundry.sln
dotnet build Foundry.sln
dotnet test Foundry.sln
```

The broker starts on `http://127.0.0.1:57420`:

```text
dotnet run --project src/Foundry.Broker
```

### Python Discord bot

```text
cd bot
pip install -r requirements.txt
cp bot_config.example.json bot_config.json
# Edit bot_config.json — add your Discord token (never commit this file)
python foundry_bot.py
```

## Branch and PR conventions

- Branch naming: `feature/{description}`, `fix/{description}`, `docs/{description}`, `refactor/{description}`
- Commit messages: imperative mood (`Add IAgent interface`, `Fix job retention worker`)
- One concern per PR
- PRs must pass `dotnet build` and `dotnet test` before merge
- Docs-only PRs do not require test changes

## Adding a new agent

1. Create a golden eval set under `evals/<agent-name>/goldens.jsonl` with at least 20 hand-labeled examples.
   See [`evals/README.md`](./evals/README.md) for the required format.
2. Implement the `IAgent` interface in `src/Foundry.Core/Agents/`.
3. Run in **shadow mode** (post comments, do not take write actions) for at least two weeks.
4. Verify the eval pass rate against the golden set before enabling write actions.

## Documentation

When you change code, update the relevant docs in the same PR. See the documentation currency table
in [`.github/copilot-instructions.md`](./.github/copilot-instructions.md) for the specific mapping.
