module Iron.Tests.AgentEnvTests

open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open IronKernel.Eval
open Iron.Host

/// Fresh agent environment or test failure.
let private agentEnv () =
    match AgentEnv.create () with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 (env, tags) -> env, tags

/// Evaluate one expression; errors surface as `Status` (Repl.evalString traps).
let private evalIn env expr =
    Repl.evalString env (newContinuation env) expr

[<Fact>]
let ``agent env evaluates ordinary Kernel code`` () =
    let env, _ = agentEnv ()
    match evalIn env "(eqv? (+ 1 2) 3)" with
    | Bool true -> ()
    | other -> failwith ("expected #t, got " + showVal other)

[<Fact>]
let ``injected host primitive returns the product version`` () =
    let env, _ = agentEnv ()
    match evalIn env "(iron/host-version)" with
    | Obj o -> Assert.Equal(AgentEnv.version, o :?> string)
    | other -> failwith ("expected version string, got " + showVal other)

[<Fact>]
let ``effect tags are bound as prompt tags`` () =
    let env, tags = agentEnv ()
    match evalIn env "iron/llm-call" with
    | PromptTag id ->
        match tags.llmCall with
        | PromptTag expected -> Assert.Equal(expected, id)
        | _ -> failwith "host-side tag is not a prompt tag"
    | other -> failwith ("expected prompt tag, got " + showVal other)

[<Fact>]
let ``effect tags are fresh per session`` () =
    let _, first = agentEnv ()
    let _, second = agentEnv ()
    Assert.NotEqual(first.llmCall, second.llmCall)

[<Fact>]
let ``raw CLR interop is not reachable from the agent env`` () =
    let env, _ = agentEnv ()
    // `.` `new` `clr-type` are filtered out of safe-profile environments, and
    // the unbound-atom CLR sugar rewrites to `.`, which is also unbound.
    match evalIn env "(clr-type \"System.IO.File\")" with
    | Status message -> Assert.Contains("unbound", message.ToLowerInvariant())
    | other -> failwith ("expected an error, got " + showVal other)

[<Fact>]
let ``source loading is not reachable from the agent env`` () =
    let env, _ = agentEnv ()
    match evalIn env "(load \"kernel.ikr\")" with
    | Status message -> Assert.Contains("unbound", message.ToLowerInvariant())
    | other -> failwith ("expected an error, got " + showVal other)

[<Fact>]
let ``agent can perform against a host-handled effect tag`` () =
    // The host installs a handler around agent code with `prompt`; the agent
    // performs against the injected tag. This is the M0 proof of the whole
    // control-plane shape (real handlers arrive with the LLM bridge).
    let env, _ = agentEnv ()
    let program =
        "(prompt iron/llm-call \
           (lambda (payload resume-llm) (resume resume-llm (list payload 42))) \
           (perform iron/llm-call \"hello\"))"
    let value = evalIn env program
    Assert.Equal("(\"hello\" 42)", showVal value)
