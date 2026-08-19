# The TDD agent

A [jern](https://github.com/jern-ai/jern) agent that works strictly
test-first — **enforced, not requested**. Editing an implementation file is
refused by the agent's own loop until a failing test run has been observed;
once the tests pass again, the next change must again start with a failing
test.

```
$ jern run --agent agents/tdd "Add a subtract function to lib.sh"
→ edit_file                    # the model jumps straight to lib.sh…
                               # …and gets back, as a tool error:
                               # "TDD gate: implementation edits are locked
                               #  until a failing test exists…"
→ edit_file                    # so it writes the test first
→ shell (tests after edit)
TDD: red — implementation edits unlocked
→ edit_file                    # now the implementation is allowed
→ shell (tests after edit)
TDD: green — the next change starts with a failing test
```

## Why this is interesting

Every agent can be *asked* to do TDD in its prompt. None of them can
promise it: a prompt is a suggestion the model follows until it doesn't.
Here the red→green rule is [~35 lines of the agent's own source](src/main.ikr)
— a phase box, a `test-file?` predicate, and a gate in tool dispatch — so
the premature edit never reaches the filesystem no matter what the model
argues. The model also gets no `shell` tool: the only command this agent
ever runs is the test command, from the loop itself.

And because it's a jern agent, **the workflow has a regression suite**:
[test/tdd_test.ikr](test/tdd_test.ikr) unit-tests the gate's transitions
and replays a recorded conversation in which the model misbehaves first —
the recording *contains* the refusal, so `jern test agents/tdd` (offline,
no API key) fails on any change that weakens the gate. There's a test in
jern's own CI that deletes the gate and watches the suite catch it.

## Use it

The test command comes from `jern.json` (`"test_command": "pytest -q"`),
falling back to `sh test.sh`. Which files count as tests is the
`test-file?` function at the top of [src/main.ikr](src/main.ikr) — edit it
to match your project's convention (`string-suffix?`, `string-prefix?`,
and `string-contains?` are available), then run `jern test agents/tdd` to
prove you didn't break the gate.
