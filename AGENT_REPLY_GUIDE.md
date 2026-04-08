# How To Reply To DailyDesk Agents

This guide is for getting better results from the desks, inbox suggestions, and approval workflow in DailyDesk.

## The Core Rule

A useful reply has 4 parts:

1. `Source`
What material the agent should use.

2. `Task`
What you want it to do.

3. `Output`
What form the answer should take.

4. `Constraints`
What to ignore, how narrow to stay, and whether to ask follow-up questions.

If you leave one of those out, the agent will usually fill the gap with assumptions.

## The Best Prompt Shape

Use this pattern:

```text
Use [source].
Do [task].
Return [output].
Stay within [constraints].
```

## What Each Desk Is Good For

### Chief of Staff

Use for:
- deciding what to do next
- routing work between Suite, engineering, and business
- turning too many ideas into one practical plan

Best kinds of prompts:
- `Give me the highest-leverage next move for today based on my current queue.`
- `Turn these 3 priorities into a morning plan and a repo block.`
- `Tell me what to ignore today and why.`

### Engineering Desk

Use for:
- technical analysis and code review
- architecture tradeoffs
- implementation guidance

Best kinds of prompts:
- `Review this design and flag the highest-risk tradeoffs.`
- `Explain the concurrency model in the broker and what breaks if we change it.`

### Suite Context

Use for:
- quiet Suite awareness
- workflow interpretation
- read-only repo or product context

Best kinds of prompts:
- `Use Suite as background context only. Compare routing patterns that fit our workflow.`
- `Explain the tradeoffs of the approval flow without proposing code changes yet.`

### Business Ops

Use for:
- market research
- competitor review
- offer framing

Best kinds of prompts:
- `Research workflow automation tools and return only features relevant to production control.`
- `Compare these competitors by approval routing, audit trail, and pricing risk.`

### ML Engineer

Use for:
- understanding the ML pipeline status
- diagnosing scoring model health or data issues
- checking RAG index coverage and embedding health
- interpreting forecast anomalies or scoring drift

Best kinds of prompts:
- `What is the current ML pipeline status and when did it last run?`
- `Show me the scoring model performance and any data drift signals.`
- `What is the embedding coverage for my imported knowledge documents?`
- `Explain what the analytics say about recent PR scoring trends.`

## Understanding Agent Response Sections

Each desk structures its answers using named sections.

### Chief of Staff

| Section | What It Contains |
|---------|-----------------|
| `NEXT MOVE` | The single highest-leverage action for right now |
| `WHY` | The reasoning that makes this the right move |
| `HANDOFF` | Where the work should go next (Suite, engineering, or business) |

### Engineering Desk

| Section | What It Contains |
|---------|-----------------|
| `ANSWER` | The direct technical answer or explanation |
| `CHECKS` | Key verification points, failure modes, or risks |
| `SUITE LINK` | How this connects to Suite context or runtime signals |

### Suite Context

| Section | What It Contains |
|---------|-----------------|
| `CONTEXT` | Current Suite state, hot areas, or relevant workflow background |
| `TRUST` | Suite availability and runtime trust status |
| `WHY IT MATTERS` | Why this context affects operator decisions right now |

### Business Ops

| Section | What It Contains |
|---------|-----------------|
| `MOVE` | The specific operating move or offer-shaping step |
| `WHY IT WINS` | The reason this move produces real value |
| `WHAT TO PROVE` | The measurable proof point or next validation step |

### ML Engineer

| Section | What It Contains |
|---------|-----------------|
| `ML STATUS` | Current pipeline state, last run timestamp, and component health |
| `INSIGHTS` | Key findings from analytics, forecast, or embedding results |
| `RECOMMENDATIONS` | Specific actions to improve pipeline health |
| `SUITE INTEGRATION` | How ML results connect to Suite workflow or production context |

## Know The Difference Between Desk Chat And Inbox Work

### Desk Chat

Desk chat is where you ask directly for an answer, explanation, or research result.

### Inbox

Inbox is where agents put follow-through suggestions.

Use it when the app proposes:
- a research follow-up
- a business next move
- a Suite-adjacent workflow investigation
- a repo or implementation proposal

Inbox items are proposed moves, not finished work.

## What Approval Buttons Actually Mean

### `Approve only`

Records your decision. Does **not** start execution. Item moves to `Approved next`.

Use when you agree with the direction but are not ready to start it yet.

### `Approve & queue`

Records your decision and stages the item for follow-through.

### `Approve & run`

Records your decision and starts the follow-up immediately.

Use when the item is clearly scoped and you want the result now.

### `Queue`

Stages an already approved or self-serve item.

### `Run now`

Starts the selected item immediately.

## Workflow Templates

Workflow templates are named sequences of background jobs that run in order.

### Built-In Templates

#### Daily Run

Runs the full daily pipeline: ML Pipeline, Export Suite Artifacts, and Index Knowledge Documents.

Use when you want all ML and knowledge state refreshed in one step.

Steps: ML Pipeline → Export Suite Artifacts → Index Knowledge Documents

#### Knowledge Refresh

Re-indexes all knowledge documents and updates embeddings.

Use when you have imported new documents and want them indexed immediately.

Steps: Index Knowledge Documents → Refresh Document Embeddings

### Running A Template

Templates can be run from the workflow panel or via the Approve & run path in the inbox.
If you want results immediately, use `Run now` after selecting the template.
If you want it staged for later, use `Queue`.

## Best Reply Patterns For ML Engineer

### Check Pipeline Health

```text
Show me the current ML pipeline status.
Return when it last ran, what components are active, and any anomalies.
```

### Diagnose Scoring Drift

```text
Use my current ML analytics.
List any scoring drift signals and what the forecast says about model health.
Return one recommended action.
```

### Check Knowledge Coverage

```text
What is the current embedding and indexing coverage for my imported knowledge documents?
Return document count, embedding dimension, and any gaps.
```

### Interpret Forecast

```text
Use my current ML forecast.
Explain the trend and flag any anomalies.
Return one concrete next step.
```

## Best Reply Patterns For Research

If the answer needs current web facts, say so directly.

Use:

```text
/research [query]
```

Or:

```text
Use live research for this.
Compare [thing A] vs [thing B].
Return only the differences that matter for [decision].
Ignore generic marketing claims.
```

## Best Reply Patterns For Chief Of Staff

```text
I have 90 minutes.
Use my current queue and open approvals.
Tell me the single highest-leverage move and the next 2 backup moves.
```

```text
I have Suite work, ML pipeline work, and business research competing right now.
Route the day into morning, midday, and evening blocks.
Keep it realistic and cut anything non-essential.
```

## Best Reply Patterns For Suite Context

```text
Use Suite as background context only.
Explain the routing patterns we should probably support.
Return a short list of states, transitions, and operator risks.
Do not suggest code changes yet.
```

## Best Reply Patterns For Business Ops

```text
Research workflow automation tools for engineering teams.
Return only features tied to revision control, audit trail, and delivery control.
Ignore CRM, billing, and generic PM features.
```

## What Usually Causes Bad Answers

- asking for too much in one turn
- not telling the agent what source to use
- not saying whether you want analysis, research, or a decision
- not saying what to ignore
- approving an item and expecting that alone to run it
- using replies like `yes`, `ok`, or `look into this` with no scope

## The Fastest Way To Get Good Results

Use this checklist before you hit send:

1. Did I say what source to use?
2. Did I say what exact output I want?
3. Did I say what to ignore?
4. Do I want approval only, or do I want the work to start now?
5. If this needs current facts, did I explicitly ask for research?

## Current Practical Rule

If you want the agent to actually do work now, your best choices are usually:

- a direct desk prompt
- `Approve & run`
- `Run now`

If you use `Approve only`, assume the work has **not** started yet.
