module Jern.Tests.BudgetTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

/// A bridge that answers every call identically (optionally with usage).
let private fixedBridge (usage: string option) (calls: int ref) : AnthropicBridge.LlmBridge =
    fun _ ->
        calls.Value <- calls.Value + 1
        let usageField =
            match usage with
            | Some u -> sprintf ""","usage":%s""" u
            | None -> ""
        Choice2Of2(
            Json.deserialize
                (sprintf """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"hi"}]%s}""" usageField))

/// Agent code that performs n model calls in a row.
let private callLoop n =
    sprintf
        """(define loop (lambda (n)
             (if (<= n 0)
                 "done"
                 (sequence
                   (perform jern/llm-call (list :messages (vector)))
                   (loop (- n 1))))))
           (loop %d)""" n

let private budgetOf pairs =
    pairs |> List.collect (fun (k, v: int) -> [ Keyword k; Obj(v :> obj) ]) |> ofList

let private sessionWith budget approver bridge =
    let root = Path.Combine(Path.GetTempPath(), "jern-budget-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let config =
        { Session.configIn root bridge with
            budget = budget
            approver = approver }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 s -> s

let private sessionWithHardBudget limit approver bridge trace =
    let root = Path.Combine(Path.GetTempPath(), "jern-hard-budget-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let hardBudget = Session.HardTokenBudget limit
    let config =
        { Session.configIn root bridge with
            approver = approver
            traceSink = trace
            hardTokenBudget = Some hardBudget }
    match Session.createWith config with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session -> session, hardBudget

[<Fact>]
let ``an exhausted budget with approval denied ends the run`` () =
    let calls = ref 0
    let asked = ResizeArray<string>()
    let session =
        sessionWith (budgetOf [ "llm_calls", 2 ])
            (Some(fun description ->
                asked.Add description
                false))
            (fixedBridge None calls)
    match Session.runSource session "budget-test" (callLoop 5) with
    | Choice2Of2 value -> failwith ("expected the run to end, got " + showVal value)
    | Choice1Of2 error ->
        let message = showError error
        Assert.Contains("budget exhausted after 2 model calls", message)
        // The third call never reached the provider.
        Assert.Equal(2, calls.Value)
        Assert.Contains("grant another round", Assert.Single asked)

[<Fact>]
let ``approving an exhausted budget grants another round`` () =
    let calls = ref 0
    let asked = ResizeArray<string>()
    let session =
        sessionWith (budgetOf [ "llm_calls", 2 ])
            (Some(fun description ->
                asked.Add description
                true))
            (fixedBridge None calls)
    match Session.runSource session "budget-test" (callLoop 5) with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value ->
        Assert.Equal("done", (match value with Obj (:? string as s) -> s | other -> showVal other))
        Assert.Equal(5, calls.Value)
        // Exhausted before calls 3 and 5: two rounds granted.
        Assert.Equal(2, asked.Count)

[<Fact>]
let ``token budgets account response usage`` () =
    let calls = ref 0
    let asked = ResizeArray<string>()
    let session =
        sessionWith (budgetOf [ "tokens", 50 ])
            (Some(fun description ->
                asked.Add description
                false))
            (fixedBridge (Some """{"input_tokens":30,"output_tokens":10}""") calls)
    match Session.runSource session "budget-test" (callLoop 5) with
    | Choice2Of2 value -> failwith ("expected the run to end, got " + showVal value)
    | Choice1Of2 error ->
        // 40 tokens per call: call 1 (0 spent) and call 2 (40 < 50) proceed;
        // call 3 finds 80 >= 50 and must ask.
        Assert.Equal(2, calls.Value)
        Assert.Contains("80 tokens", showError error)

[<Fact>]
let ``hard token budget cannot be renewed by approval`` () =
    let calls = ref 0
    let asked = ref 0
    let trace = ResizeArray<string>()
    let session, hardBudget =
        sessionWithHardBudget 50L
            (Some(fun _ -> asked.Value <- asked.Value + 1; true))
            (fixedBridge (Some """{"input_tokens":20,"output_tokens":10}""") calls)
            (Some trace.Add)
    let source =
        """(sequence
              (perform jern/llm-call (list :messages (vector)))
              (perform jern/llm-call (list :messages (vector)))
              (perform jern/log (list :event "after-hard-cap"))
              "done")"""
    match Session.runSource session "hard-budget-test" source with
    | Choice2Of2 value -> failwith ("expected the run to end, got " + showVal value)
    | Choice1Of2 error ->
        Assert.Contains("hard token budget of 50 exceeded with 60 tokens", showError error)
        Assert.Equal(2, calls.Value)
        Assert.Equal(60L, hardBudget.Spent)
        Assert.Equal(0, asked.Value)
        Assert.Contains(trace, fun line -> line.Contains "\"event\":\"llm-response\"")
        Assert.Contains(trace, fun line -> line.Contains "\"event\":\"hard-token-budget-denied\"")
        Assert.DoesNotContain(trace, fun line -> line.Contains "\"event\":\"after-hard-cap\"")
        let crossingResponse =
            trace
            |> Seq.mapi (fun index line -> index, line)
            |> Seq.filter (fun (_, line) -> line.Contains "\"event\":\"llm-response\"")
            |> Seq.last
            |> fst
        let denial =
            trace |> Seq.findIndex (fun line -> line.Contains "\"event\":\"hard-token-budget-denied\"")
        Assert.True(crossingResponse < denial)
        let tracePath = Path.Combine(Path.GetTempPath(), "jern-hard-budget-receipt-" + Guid.NewGuid().ToString("N") + ".jsonl")
        try
            File.WriteAllLines(tracePath, trace)
            match Receipt.ofTrace tracePath with
            | Error message -> failwith message
            | Ok receipt ->
                Assert.Equal(40L, receipt.inputTokens)
                Assert.Equal(20L, receipt.outputTokens)
                Assert.True receipt.hardTokenBudgetDenied
        finally
            File.Delete tracePath

[<Fact>]
let ``hard token budget fails closed without provider usage`` () =
    let calls = ref 0
    let session, hardBudget =
        sessionWithHardBudget 50L (Some(fun _ -> true)) (fixedBridge None calls) None
    match Session.runSource session "hard-budget-test" (callLoop 1) with
    | Choice2Of2 value -> failwith ("expected the run to end, got " + showVal value)
    | Choice1Of2 error ->
        Assert.Contains("hard token budget cannot verify usage", showError error)
        Assert.Equal(1, calls.Value)
        Assert.Equal(0L, hardBudget.Spent)

[<Fact>]
let ``no configured budget means no interference`` () =
    let calls = ref 0
    let session = sessionWith Nil (Some(fun _ -> failwith "must not ask")) (fixedBridge None calls)
    match Session.runSource session "budget-test" (callLoop 4) with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 _ -> Assert.Equal(4, calls.Value)

[<Fact>]
let ``jern.json budget parses and the CLI shape wins`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-budgetconf-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        File.WriteAllText(Path.Combine(root, "jern.json"),
                          """{ "budget": { "llm_calls": 30, "tokens": 200000 } }""")
        match Providers.load root with
        | Error message -> failwith message
        | Ok config ->
            Assert.Equal(Some 30, config.budgetLlmCalls)
            Assert.Equal(Some 200000, config.budgetTokens)
            Assert.Equal("""{"llm_calls":30,"tokens":200000}""",
                         Json.serialize (Providers.budget config))
    finally
        Directory.Delete(root, true)
