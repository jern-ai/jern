module Jern.Tests.UiTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Xunit
open IronKernel.Ast
open Jern.Host
open Jern.Cli

/// Drive the whole web UI path: POST a message, watch the SSE stream, answer
/// the approval card, and see the turn finish with the file edited.
[<Fact>]
let ``ui serves a full turn with an interactive approval`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-ui-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
    let repoAgentDir = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))
    let mutable turn = 0
    let makeBridge (onText: string -> unit) : AnthropicBridge.LlmBridge =
        fun _ ->
            turn <- turn + 1
            match turn with
            | 1 ->
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"u1","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}""")
            | _ ->
                onText "Fixed the typo."
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed the typo."}]}""")
    let server =
        Ui.start
            { root = root
              makeBridge = makeBridge
              agentSources = Session.agentPackageSources repoAgentDir
              agentConfig = Nil
              mcpServers = []
              budget = Nil
              modelLabel = "test/model"
              port = 0 }
    Task.Run(server.run) |> ignore
    try
        use client = new HttpClient(BaseAddress = Uri server.url, Timeout = TimeSpan.FromSeconds 15.0)
        use sseClient = new HttpClient(BaseAddress = Uri server.url)
        // Collect SSE events on a background task (its own client: the
        // stream never ends, so it must not share the POSTs' connection).
        let events = ConcurrentQueue<string>()
        let sse =
            Task.Run(fun () ->
                use stream = sseClient.GetStreamAsync("/events").Result
                use reader = new StreamReader(stream)
                let mutable go = true
                while go do
                    match reader.ReadLine() with
                    | null -> go <- false
                    | line when line.StartsWith "data: " -> events.Enqueue(line.Substring 6)
                    | _ -> ())
        let awaitEvent (marker: string) =
            let deadline = DateTime.UtcNow.AddSeconds 20.0
            let mutable found = None
            while found.IsNone && DateTime.UtcNow < deadline do
                match events.TryDequeue() with
                | true, e -> if e.Contains marker then found <- Some e
                | _ -> System.Threading.Thread.Sleep 25
            match found with
            | Some e -> e
            | None -> failwithf "no SSE event containing %s" marker

        let post (path: string) (body: string) =
            let response = client.PostAsync(path, new StringContent(body, Encoding.UTF8)).Result
            int response.StatusCode

        // State arrives on connect.
        awaitEvent "\"model\":\"test/model\"" |> ignore
        Assert.Equal(202, post "/message" """{"text":"Fix the typo in greeting.txt"}""")
        // The default policy asks before edit_file; answer via the card.
        let approval = awaitEvent "\"type\":\"approval\""
        Assert.Contains("edit_file", approval)
        Assert.Contains("greeting.txt", approval)
        let id =
            match Tools.plistTryGet "id" (Json.deserialize approval) with
            | Some (Obj (:? string as s)) -> s
            | _ -> failwith "approval event carries no id"
        Assert.Equal(200, post "/approve" (sprintf """{"id":"%s","approved":true}""" id))
        // Streamed text, the traced tool call, and completion all arrive.
        awaitEvent "Fixed the typo." |> ignore
        awaitEvent "\"type\":\"done\"" |> ignore
        Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(root, "greeting.txt")))
        Assert.Equal(2, turn)
        // A second message while idle is accepted; while busy it would 409.
        Assert.Equal(400, post "/message" """{"text":""}""")
        sse |> ignore
    finally
        server.stop ()
        Directory.Delete(root, true)

[<Fact>]
let ``ui denies an approval and the agent sees the refusal`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-uideny-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
    let repoAgentDir = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))
    let mutable sawDecline = false
    let makeBridge (_: string -> unit) : AnthropicBridge.LlmBridge =
        fun request ->
            let json = Json.serialize request
            if json.Contains "declined" then
                sawDecline <- true
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Understood."}]}""")
            else
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"u1","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}""")
    let server =
        Ui.start
            { root = root
              makeBridge = makeBridge
              agentSources = Session.agentPackageSources repoAgentDir
              agentConfig = Nil
              mcpServers = []
              budget = Nil
              modelLabel = "test/model"
              port = 0 }
    Task.Run(server.run) |> ignore
    try
        use client = new HttpClient(BaseAddress = Uri server.url, Timeout = TimeSpan.FromSeconds 15.0)
        use sseClient = new HttpClient(BaseAddress = Uri server.url)
        let events = ConcurrentQueue<string>()
        Task.Run(fun () ->
            use stream = sseClient.GetStreamAsync("/events").Result
            use reader = new StreamReader(stream)
            let mutable go = true
            while go do
                match reader.ReadLine() with
                | null -> go <- false
                | line when line.StartsWith "data: " -> events.Enqueue(line.Substring 6)
                | _ -> ()) |> ignore
        let awaitEvent (marker: string) =
            let deadline = DateTime.UtcNow.AddSeconds 20.0
            let mutable found = None
            while found.IsNone && DateTime.UtcNow < deadline do
                match events.TryDequeue() with
                | true, e -> if e.Contains marker then found <- Some e
                | _ -> System.Threading.Thread.Sleep 25
            match found with
            | Some e -> e
            | None -> failwithf "no SSE event containing %s" marker
        // Wait for the state hello: only then is the SSE client subscribed.
        awaitEvent "\"type\":\"state\"" |> ignore
        client.PostAsync("/message", new StringContent("""{"text":"Fix it"}""", Encoding.UTF8)).Result |> ignore
        let approval = awaitEvent "\"type\":\"approval\""
        let id =
            match Tools.plistTryGet "id" (Json.deserialize approval) with
            | Some (Obj (:? string as s)) -> s
            | _ -> failwith "no id"
        client.PostAsync("/approve", new StringContent(sprintf """{"id":"%s","approved":false}""" id, Encoding.UTF8)).Result |> ignore
        awaitEvent "\"type\":\"done\"" |> ignore
        Assert.True sawDecline
        // The refused edit never touched the file.
        Assert.Equal("helo world\n", File.ReadAllText(Path.Combine(root, "greeting.txt")))
    finally
        server.stop ()
        Directory.Delete(root, true)
