# Contributing to jern

jern is Apache-2.0 (see [LICENSE](LICENSE)). Contributions are welcome under
the same license.

## Developer Certificate of Origin

We use the [DCO](https://developercertificate.org/) rather than a CLA: sign
off every commit to certify you have the right to contribute it.

```bash
git commit -s
```

adds the `Signed-off-by:` trailer. By signing off you agree to the DCO.

## Practicalities

- Building needs a sibling checkout of
  [IronKernel](https://github.com/ironkernel-lang/IronKernel); see the README.
- `dotnet test Jern.slnx` must be green, including the agents' own suites
  (`jern test agents/default`, `jern test agents/docs`).
- Changes to agent behavior fail fixture replay by design; re-record with
  `jern test --record` (or the generator tests) and commit the new fixtures
  with the change.
- Gaps that belong in the language (runtime primitives, parser, effects) go
  upstream to IronKernel as their own PRs.
