**Iron Product Spec (Working Title: Iron / IronAgent)**

### 1. Vision & Positioning

**One-liner**  
A programmable agent platform for software engineering. Cursor-level interactive experience + Devin-level autonomy, with the entire agent system expressed as inspectable, versionable, capability-secure IronKernel (https://ironkernel.org/) code.

**Positioning**  
- Against Cursor: equally polished daily driver, but agents and policies are real programs you can open, edit, test, and package.  
- Against Devin: comparable long-horizon autonomy and sandbox power, plus language-native isolation, composability, and auditability that current systems lack.  
- Unique claim: “The coding agent whose brain is fully programmable and capability-secure by construction.”

### 2. Target Users

| Persona | Primary Need | How they use Iron |
|---------|--------------|-------------------|
| Individual developer | Fast interactive coding + occasional autonomous tasks | IDE + CLI, mostly natural language |
| Power user / staff engineer | Custom tools, specialized agents, team standards | Opens IronKernel source, authors operatives & policies |
| Engineering team / platform | Shared, governed agents + compliance | Packages, capability profiles, registry, cloud policies |
| Embedder / product team | Agent runtime inside their own tools | Iron Runtime / SDK |

### 3. Product Surfaces

**3.1 Iron Workspace (primary interactive surface)**  
Agent-first IDE (VS Code fork or equivalent + dedicated agent shell).  
- Chat + Agent mode + Plan mode  
- Multi-file edits with diff review  
- Parallel local + cloud agents in a sidebar  
- Integrated terminal, browser/computer-use preview, codebase semantic search  
- One-click “Open agent source” that drops into the IronKernel definition of the running agent  
- Skills / tools / agent templates from the registry appear as first-class UI objects  

**3.2 Iron CLI**  
`iron` (or `ik agent`) for headless and scripted use.  
- `iron agent run "…"`  
- Session resume, parallel agents, output formats (text, JSON, PR)  
- Works against local workspace or remote/cloud sandboxes  

**3.3 Iron Cloud Agents**  
Long-running autonomous execution.  
- Isolated environments (Linux primary; Windows/macOS secondary)  
- Pre-built “builds” (warm environments with deps installed)  
- Computer-use / desktop testing, browser, shell, git, CI hooks  
- Stacked PRs, self-review, human approval gates  
- Multi-agent coordination (parent agent spawns managed children)  
- Dashboard for sessions, costs, traces, capability audit logs  

**3.4 Iron Packages & Registry**  
First-class distribution for everything agent-related.  
- Tools, skills, agent definitions, effect handlers, capability policies, multi-agent patterns  
- Packaged as IronKernel projects (`.ikproj` + source + contracts)  
- Versioned, testable, dependency-resolved like normal libraries  
- Marketplace + private/team registries  

**3.5 Iron Runtime / SDK**  
Embeddable .NET library + IronKernel runtime.  
- Used by the IDE, CLI, and Cloud  
- Available to third parties who want to host Iron agents inside their own products  

### 4. Core User Journeys

**Interactive (Cursor-style)**  
1. Open workspace → describe goal in natural language.  
2. Agent plans (optionally shows IronKernel plan operative).  
3. Executes with live diffs, terminal output, and tool traces.  
4. User reviews, steers, or opens the agent source to adjust behavior mid-session.  
5. Commits or continues.

**Autonomous (Devin-style)**  
1. Assign ticket / high-level goal (from IDE, CLI, Linear/GitHub, or dashboard).  
2. Cloud agent starts in a prepared environment.  
3. Plans, implements, tests (including computer-use where relevant), self-reviews, opens stacked PRs.  
4. Human receives reviewable artifacts + full effect/capability trace.  
5. Optional: human intervenes via chat or by editing the running agent’s IronKernel state.

**Authoring / Customization (the differentiator)**  
1. `iron new agent my-specialist` or “Fork this agent” from UI.  
2. Edit operatives, environments, effect handlers, and contracts in IronKernel.  
3. Attach capability profiles (what the agent may touch).  
4. `iron test`, run locally, package, publish to team registry.  
5. Other developers or the Cloud runtime consume the package as a first-class agent/tool.

### 5. IronKernel as the Core Runtime

Everything that makes an agent an agent is expressed in IronKernel:

- **Tools** → combiners with contracts (schema + purity + effect summary)  
- **Agent loops** → operatives using tagged effects (`llm-call`, `tool-call`, `observe`, `approve`, …)  
- **Context / isolation** → first-class environments + capability profiles  
- **Multi-agent** → environments + effects + delimited continuations for coordination and speculative work  
- **Safety & policy** → language-level capability checks + effect handlers that can interpose, log, or require approval  
- **Skills & memory** → libraries and data structures that agents import into their environments  

The IDE and Cloud are thin, polished shells over this runtime. Default agents ship as well-tested IronKernel packages; power users replace or extend them.

### 6. Key Technical Components

- IronKernel runtime (interpreter + Expression-tree compiler + capability enforcement)  
- Effect system + deep handlers for the agent control plane  
- Tool bridge to Microsoft.Extensions.AI / Microsoft Agent Framework (and direct provider SDKs)  
- Codebase intelligence layer (indexing, semantic search, AST-aware edits)  
- Sandbox & environment manager (local + cloud builds, snapshots, computer-use)  
- Observability (structured traces of effects, tool calls, capability decisions, token/cost)  
- Package system & registry  
- VS Code / agent-host protocol compatibility for broader IDE reach  

### 7. Packaging & Distribution Model

- **Free / Hobby**: Local IDE + limited cloud credits, community packages  
- **Pro**: Full interactive agents, higher cloud limits, private packages  
- **Team / Enterprise**: Shared registries, org-wide capability policies, audit logs, SSO, private cloud/VPC options, compliance features  
- Runtime/SDK licensed for embedding (separate commercial terms)

Packages are the unit of extensibility and governance. A team can ship “OurSafeCodingAgent v1.4” that only has the exact tools and profiles the security team approved.

### 8. Differentiation Summary

| Capability | Cursor / Devin today | Iron |
|------------|----------------------|------|
| Interactive polish | Excellent | Match |
| Long-horizon autonomy | Strong (especially Devin) | Match + stronger isolation story |
| Custom agent behavior | Prompts + plugins / skills | Full IronKernel programs |
| Safety / least privilege | Sandbox + policy layers | Language-native capabilities + environments |
| Auditability | Traces | Traces + inspectable source of the agent itself |
| Composability | Limited | First-class (packages, environments, effects) |
| Testability of agents | Weak | `iron test` + contracts |

### 9. High-Level Roadmap

**Phase 0 – Foundation (current work)**  
Solidify the agent/tool framework inside IronKernel (effects, `define-tool`, environments, contracts, basic LLM bridge).

**Phase 1 – Local Interactive MVP**  
- CLI + minimal VS Code extension or simple agent shell  
- Working ReAct / function-calling agents with real tools (file edit, terminal, git)  
- Ability to open and edit the agent definition  

**Phase 2 – Full Workspace**  
- Polished agent-first IDE experience  
- Codebase indexing, parallel agents, good review UX  
- Package system + public registry  

**Phase 3 – Cloud Autonomy**  
- Sandboxed long-running agents, builds, computer-use, PR workflow  
- Multi-agent coordination and human-in-the-loop gates  

**Phase 4 – Platform**  
- Enterprise controls, private registries, embedding SDK, advanced multi-agent patterns, formal capability auditing  

### 10. Open Decisions (for later refinement)

- Exact product name and branding  
- VS Code fork vs. deep extension + separate agent host (Microsoft’s Agent Host Protocol direction is relevant)  
- Primary model strategy (own routing layer vs. pure bring-your-own + curated defaults)  
- How aggressively to expose IronKernel syntax in the default UI vs. keep it power-user only  
- Sandbox technology choices (Firecracker, containers, full VMs, computer-use stack)

---

This spec turns the language work into a coherent product that can actually sit on the same field as Cursor and Devin while leaning hard into IronKernel’s unique strengths.

Would you like me to expand any section into more detail next — for example the exact shape of an IronKernel agent package, the capability/profile model, the Phase 1 MVP feature list, or the IDE “open agent source” experience?