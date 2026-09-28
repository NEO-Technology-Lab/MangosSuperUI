# MangosSuperUI — agent instructions (any agent: Claude, Codex, Qwen, a human)

Standing brief for whoever works this repo. Agent-agnostic on purpose; tool-specific loaders
import it (`CLAUDE.md` is one line: `@AGENTS.md`). The client repo has its own `AGENTS.md`
with the same shape; the C++ core lives on the Linux box, not on this machine.

## 1. Find code with the locator, not with grep (2026-09-08)

The three repos (this web app, `MSUIClient`, the vmangos C++ core) are indexed by one local
service, the **superui-locator**, at `http://127.0.0.1:5077`. It holds every type and member of
both C# repos live from the working tree (a saved file is re-indexed within a second), the
libclang graph of the C++ core, string literals, leading comments, and the cross-repo seams
(bridge message names, SUI opcodes, shared tables, twin files). Measured reason: before it
existed, sessions spent a median of 27 discovery calls before the first edit and 87 % of grep
strings were one-offs.

**Rule:** before any tree-wide grep, `find`, `Select-String`, `sed -n` walk, or "where is X"
reasoning, ask the locator. Fall back to grep only when it returns nothing after two phrasings,
and say so.

| Need | MCP tool (Claude Code, Codex with MCP) | curl (any agent) |
|---|---|---|
| files/entities a task touches, with spans and why | `locate(task)` | `curl -s "http://127.0.0.1:5077/locate?task=..."` |
| find a symbol, comment text, message/opcode/table name, UI/log text | `search(q, repo?, kind?)` | `.../search?q=...&repo=web|cli|core` |
| structure of a file or entity before reading it | `outline(file=...)` / `outline(id=...)` | `.../outline?file=GameLoop.Net.cs` |
| callers/callees, reads/writes, cross-repo seam/port edges | `neighbours(id, types?)` | `.../neighbours?id=...&types=calls,seam` |
| numbered source for exactly one span (C++ comes live from the box) | `read(id)` / `read(file,start,end)` | `.../read?id=...` |
| literal text in the C++ tree | `grep(q, dir?)` | `.../grep?q=...&dir=SuiBots` |
| is it up | `stats` | `.../stats` |

Ids look like `web:MangosSuperUI.Services.BotBridgeService::SendToBotAsync(int,string,object)`,
`cli:MSUIClient.GameLoop::ControlledGuid`, `core:AiBotAI.Combat/HandleCombatStalemate`; a unique
suffix (`BotBridgeService::SendToBotAsync`) is accepted everywhere. Order of operations:
`locate` → `outline` → `neighbours` → `read` one span at a time (≤ 400 lines). Do not `read`
whole files to find a method.

**Enforcement (Claude Code):** a PreToolUse hook (`SourceMapper/Locator/hooks/locate-first.py`, registered in
`.claude/settings.json`) denies tree-wide searches (Grep without a file path, recursive grep/rg/Select-String)
until a locator tool has been called in the last 15 minutes; file-scoped searches always pass, and the hook
stands down when the host is not running. Codex/Qwen have no hook: the rule above is the contract.

If `stats` does not answer, the host is not running: start it (`dotnet run -c Release` in
`C:\Users\nico\source\repos\SourceMapper\Locator`, or the `locator` entry in `.claude/launch.json`)
or tell the owner — never grep around it silently. Razor views (including their inline
`<script>` functions), `wwwroot/js` functions, SQL/py scripts, and every markdown doc (root design
docs, `docs/`, the client's `shared_docs/`, the assistant memory) are indexed too, with sections,
dated status lines and doc→code mention edges; JSON and binary assets are not.

## 2. Standing rules

1. Never commit, push, or create branches/worktrees on your own; leave work unstaged. Never
   drop backup copies (`.bak`, `.pre*`) in the tree.
2. The app is published from Windows to the Linux box; runtime files are never local. Ship
   diagnostics as app endpoints, not as "check this file on the box".
3. `server-config.json` is owner-maintained with Linux paths; never swap it to test locally.
4. All game assets load from the client MPQs on demand; never read pre-extracted `wwwroot` files.
5. Design docs at the root (`WEAPON_FORGE.md`, `ARMOR_FORGE.md`, `WORLD_STATE.md`, …) carry the
   proven-vs-pending state of each feature; read the relevant one before changing that feature
   and append a dated note when the state changes.

## 3. Where things are

- Web app source: `MangosSuperUI/` (Controllers, Services/<Area>, Views, wwwroot/js, BotLogic, Hubs).
- Bot bridge contract: `docs/WIRE_PROTOCOL.md`; the C# side is `Services/BotBridgeService.cs`,
  the C++ side `SuiBots/AiBotAIBridge.cpp` on the box (`locate "bridge message"` shows both).
- Locator design and as-built notes: `C:\Users\nico\source\repos\SourceMapper\LOCATOR.md`.
