# Jern.Agent.Default

The default [jern](https://github.com/jern-ai/jern) coding agent: the
function-calling loop, prompts, and tool wiring as readable IronKernel source.

- `src/main.ikr` — the whole brain (~100 lines). Edit and rerun; no recompile.
- `test/` — the agent's test suite; `jern test` replays it deterministically
  against the recorded LLM fixtures in `test/fixtures/`.
