# Daily Desk

Separate Windows desktop companion for:

- electrical-engineering study and challenge prompts
- read-only awareness of the local `Suite` repo
- career positioning based on active work
- future monetization thinking grounded in `Suite` docs

## Current shape

- Native WPF shell
- Dark slate / copper operator UI
- Workspace-first adaptive shell with:
  - `Wide`, `Medium`, and `Focused` layout modes
  - collapsible crew/signals drawers at narrower widths
  - a dedicated bottom job dock instead of a fixed approval strip
- 6 primary views only:
  - `Operator`
  - `Training Session`
  - `Research`
  - `Repo`
  - `Inbox`
  - `Library`
- Local Ollama model discovery
- Local knowledge library under `%USERPROFILE%\Dropbox\SuiteWorkspace\Office\Knowledge` with a `Class Notes` drop zone
- Optional mapped read-only knowledge sources, including OneNote-export folders
- Dedicated `Inbox` workflow with:
  - pending approvals first
  - deferred/resolved suggestion history below
  - one detail surface for accept / defer / reject actions
  - `Open in Inbox` routing from other views instead of scattered approval buttons
- Live web research tab with:
  - current web search against live sources
  - agent-style synthesis using the existing local model rack
  - save-to-knowledge flow so research can feed later coaching
  - recurring research watchlists for operator-selected topics
  - research-to-suggestion routing so useful findings land in the desk as actionable items
- Structured training loop with:
  - one guided `Training Session` surface instead of split training/defense tabs
  - visible session stage tracking for `Plan`, `Practice`, `Defense`, `Reflection`, and `Complete`
  - generated multiple-choice practice tests
  - local scoring and stored attempt history
  - weak-topic summaries and recent-attempt tracking
  - spaced review queue with due-now and due-soon targets
  - oral-defense drills for harder reasoning practice
  - typed oral-defense answers with rubric scoring and follow-up coaching
  - saved session reflections that feed the next training prompt and daily brief
  - actionable review targets that can start a focused retest from the queue
  - adaptive recommendations tied to `Suite` context
- prompt conditioning from your own notes, PDFs, DOCX files, PPTX slide decks, and coaching instructions
- Optional Ollama generation for:
  - chief brief
  - EE challenge
  - business map
  - practice-test generation
  - oral-defense generation
- Operator layer with:
  - daily operator plan
  - autonomy policy editing by agent role
  - approval inbox for repo-touching or high-impact suggestions
  - suggestion memory with accepted / rejected / deferred outcomes
  - Suite Context refresh for quiet Suite awareness and workflow trust
  - recurring operator activity history and a career-engine scoreboard
- Read-only `Suite` snapshot from:
  - `git status`
  - recent commits
  - `docs/development/work-summary-and-todo.md`
  - `docs/development/monetization-readiness-backlog.md`
  - `README.md`

## Run

```powershell
dotnet run
```

## Settings

Use `dailydesk.settings.local.json` for workstation-local overrides such as:

- `suiteRepoPath`
- `ollamaEndpoint`
- `primaryModelProvider` (`ollama` is active today)
- `enableHuggingFaceCatalog` (catalog/discovery toggle only)
- `huggingFaceTokenEnvVar` (token env var name for future Hugging Face catalog access)
- `huggingFaceMcpUrl` (future Hugging Face MCP/catalog endpoint)
- `knowledgeLibraryPath`
- `stateRootPath`
- primary models for chief, mentor, training builder, repo coach, and business strategist

## Important behavior

- For practical prompt patterns and approval replies, see `AGENT_REPLY_GUIDE.md`
- Daily Desk is separate from `Suite`
- Daily Desk only reads `Suite` for now
- Daily Desk may generate proposals about `Suite`, but it does not mutate the repo
- Research, training, reflections, suggestion outcomes, and watchlist history are part of the local memory loop
- The knowledge library defaults to `%USERPROFILE%\Dropbox\SuiteWorkspace\Office\Knowledge`
- Office can also scan configured external read-only knowledge roots such as:
  - `C:\Users\koraj\OneDrive\Documents\OneNote Notebooks`
- Supported study file types are `.md`, `.txt`, `.pdf`, `.docx`, `.pptx`, and exported OneNote `.onepkg` packages
- The quickest way to feed EE Mentor class context is:
  - click `Open Knowledge`
  - drop your files into `%USERPROFILE%\Dropbox\SuiteWorkspace\Office\Knowledge\Class Notes`
  - click `Refresh Office`
  - check `Library` to confirm the files were loaded
- Raw OneNote notebook containers are not parsed directly, but exported `.onepkg` packages are now supported. Office will extract readable note text from the packaged `.one` content where possible.
- EE Mentor, desk chat, practice tests, and oral-defense scoring now pull relevant excerpts from imported knowledge instead of relying only on file summaries.
- Direct exported `.pdf`, `.docx`, `.pptx`, `.md`, and `.txt` files are still the cleanest source material.
- Training history is stored under `%USERPROFILE%\Dropbox\SuiteWorkspace\Office\State\training-history.json`
- The training-history file is only created after you score a practice test, score a defense answer, and save a reflection
- The `Training Session` view shows the history file path and last write state so it is obvious when memory has actually been written
- Operator memory is stored under `%USERPROFILE%\Dropbox\SuiteWorkspace\Office\State\operator-memory.json`
- PDF extraction uses the local Python environment through `Scripts\extract_document_text.py`
- monetization guidance is intentionally conservative and follows `Suite`'s own product notes
