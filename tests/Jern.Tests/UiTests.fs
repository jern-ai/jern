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

let private startServer root makeBridge agentSources agentDir model : Ui.Server =
    let server =
        Ui.start
            { root = root
              makeBridge = makeBridge
              providers = Providers.defaultConfig
              model = model
              agentDir = agentDir
              agentSources = agentSources
              agentConfig = Nil
              mcpServers = []
              budget = Nil
              auto = false
              port = 0
              // Tests trust every workspace policy and never touch the
              // real ~/.config/jern/trusted.json.
              policyTrust = (fun _ _ -> true)
              rememberPolicy = fun _ _ -> () }
    Task.Run(server.run) |> ignore
    server

/// Token-carrying HTTP client plus an SSE reader (its own connection: the
/// stream never ends, so it must not share the POSTs' connection).
type private UiClient(server: Ui.Server) =
    let baseUri = Uri(server.url.Split('?').[0])
    let client = new HttpClient(BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds 15.0)
    let sseClient = new HttpClient(BaseAddress = baseUri)
    let events = ConcurrentQueue<string>()
    do
        client.DefaultRequestHeaders.Add("X-Jern-Token", server.token)
        Task.Run(fun () ->
            use stream = sseClient.GetStreamAsync("/events?token=" + server.token).Result
            use reader = new StreamReader(stream)
            let mutable go = true
            while go do
                match reader.ReadLine() with
                | null -> go <- false
                | line when line.StartsWith "data: " -> events.Enqueue(line.Substring 6)
                | _ -> ())
        |> ignore

    member _.Http = client

    member _.AwaitEvent(marker: string) =
        let deadline = DateTime.UtcNow.AddSeconds 20.0
        let mutable found = None
        while found.IsNone && DateTime.UtcNow < deadline do
            match events.TryDequeue() with
            | true, e -> if e.Contains(marker: string) then found <- Some e
            | _ -> System.Threading.Thread.Sleep 25
        match found with
        | Some e -> e
        | None -> failwithf "no SSE event containing %s" marker

    member _.Post (path: string) (body: string) =
        let response = client.PostAsync(path, new StringContent(body, Encoding.UTF8)).Result
        int response.StatusCode

    member _.Get(path: string) =
        client.GetStringAsync(path: string).Result

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            sseClient.Dispose()

let private repoAgentDir () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default"))

let private newRoot prefix =
    let root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

/// Drive the whole web UI path: POST a message, watch the SSE stream, answer
/// the approval card, and see the turn finish with the file edited.
[<Fact>]
let ``ui serves a full turn with an interactive approval`` () =
    let root = newRoot "jern-ui-"
    File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
    let mutable turn = 0
    let makeBridge _ (onText: string -> unit) : AnthropicBridge.LlmBridge =
        fun _ ->
            turn <- turn + 1
            match turn with
            | 1 ->
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"u1","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}""")
            | _ ->
                onText "Fixed the typo."
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Fixed the typo."}]}""")
    let server =
        startServer root makeBridge (Session.agentPackageSources (repoAgentDir ())) None (Some "test/model")
    try
        use client = new UiClient(server)
        client.AwaitEvent "\"model\":\"test/model\"" |> ignore
        Assert.Equal(202, client.Post "/message" """{"text":"Fix the typo in greeting.txt"}""")
        // The default policy asks before edit_file; answer via the card.
        let approval = client.AwaitEvent "\"type\":\"approval\""
        Assert.Contains("edit_file", approval)
        let id =
            match Tools.plistTryGet "id" (Json.deserialize approval) with
            | Some (Obj (:? string as s)) -> s
            | _ -> failwith "approval event carries no id"
        Assert.Equal(200, client.Post "/approve" (sprintf """{"id":"%s","approved":true}""" id))
        client.AwaitEvent "Fixed the typo." |> ignore
        client.AwaitEvent "\"type\":\"done\"" |> ignore
        Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(root, "greeting.txt")))
        Assert.Equal(2, turn)
        Assert.Equal(400, client.Post "/message" """{"text":""}""")
    finally
        server.stop ()
        Directory.Delete(root, true)

[<Fact>]
let ``ui denies an approval and the agent sees the refusal`` () =
    let root = newRoot "jern-uideny-"
    File.WriteAllText(Path.Combine(root, "greeting.txt"), "helo world\n")
    let mutable sawDecline = false
    let makeBridge _ (_: string -> unit) : AnthropicBridge.LlmBridge =
        fun request ->
            let json = Json.serialize request
            if json.Contains "declined" then
                sawDecline <- true
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Understood."}]}""")
            else
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"u1","name":"edit_file","input":{"path":"greeting.txt","old_string":"helo","new_string":"hello"}}]}""")
    let server =
        startServer root makeBridge (Session.agentPackageSources (repoAgentDir ())) None (Some "test/model")
    try
        use client = new UiClient(server)
        client.AwaitEvent "\"type\":\"state\"" |> ignore
        client.Post "/message" """{"text":"Fix it"}""" |> ignore
        let approval = client.AwaitEvent "\"type\":\"approval\""
        let id =
            match Tools.plistTryGet "id" (Json.deserialize approval) with
            | Some (Obj (:? string as s)) -> s
            | _ -> failwith "no id"
        client.Post "/approve" (sprintf """{"id":"%s","approved":false}""" id) |> ignore
        client.AwaitEvent "\"type\":\"done\"" |> ignore
        Assert.True sawDecline
        Assert.Equal("helo world\n", File.ReadAllText(Path.Combine(root, "greeting.txt")))
    finally
        server.stop ()
        Directory.Delete(root, true)

[<Fact>]
let ``requests without the startup token are refused`` () =
    let root = newRoot "jern-uiauth-"
    let makeBridge _ _ : AnthropicBridge.LlmBridge =
        fun _ -> Choice1Of2 (IronKernel.Ast.Default "unused")
    let server = startServer root makeBridge [] None None
    try
        use bare = new HttpClient(BaseAddress = Uri(server.url.Split('?').[0]), Timeout = TimeSpan.FromSeconds 15.0)
        let noToken = bare.GetAsync("/state").Result
        Assert.Equal(403, int noToken.StatusCode)
        let wrongToken =
            bare.PostAsync("/message?token=wrong", new StringContent("""{"text":"hi"}""", Encoding.UTF8)).Result
        Assert.Equal(403, int wrongToken.StatusCode)
        let withToken = bare.GetAsync("/state?token=" + server.token).Result
        Assert.Equal(200, int withToken.StatusCode)
    finally
        server.stop ()
        Directory.Delete(root, true)

/// The brain editor: list the loaded sources, read one, save an edit, see
/// the session reload and behave differently — then run the agent's own
/// test suite from the UI. Also: the allowlist refuses foreign paths.
[<Fact>]
let ``the brain editor saves, reloads the session, and runs tests`` () =
    let root = newRoot "jern-uiedit-"
    let agentDir = newRoot "jern-uiagent-"
    Directory.CreateDirectory(Path.Combine(agentDir, "src")) |> ignore
    Directory.CreateDirectory(Path.Combine(agentDir, "test")) |> ignore
    let mainPath = Path.GetFullPath(Path.Combine(agentDir, "src", "main.ikr"))
    let agentSource =
        String.concat "\n"
            [ """(define chat-turn"""
              """  (lambda (messages text)"""
              """    (sequence"""
              """      (perform jern/log (list :marker "v1"))"""
              """      (cons (list :role "assistant" :content (vector (list :type "text" :text "v1")))"""
              """            messages))))"""
              """(define run-agent (lambda (task) "v1"))"""
              "" ]
    File.WriteAllText(mainPath, agentSource)
    File.WriteAllText(
        Path.Combine(agentDir, "test", "t.ikr"),
        """(deftest "the agent answers stably" (assert-equal (run-agent "x") (run-agent "x")))""" + "\n")
    let makeBridge _ _ : AnthropicBridge.LlmBridge =
        fun _ -> Choice1Of2 (IronKernel.Ast.Default "this agent makes no llm calls")
    let server = startServer root makeBridge [ mainPath ] (Some agentDir) None
    try
        use client = new UiClient(server)
        client.AwaitEvent "\"type\":\"state\"" |> ignore
        // The files listing carries the agent source and the (not yet
        // existing) workspace policy with its template.
        let files = client.Get("/files")
        Assert.Contains("main.ikr", files)
        Assert.Contains("policy.ikr", files)
        Assert.Contains("policy_template", files)
        // Read, then a turn behaves as v1.
        let file = client.Get("/file?path=" + Uri.EscapeDataString mainPath)
        Assert.Contains("v1", file)
        client.Post "/message" """{"text":"hi"}""" |> ignore
        client.AwaitEvent "\"marker\":\"v1\"" |> ignore
        client.AwaitEvent "\"type\":\"done\"" |> ignore
        // Save an edit: the session reloads and the next turn is v2.
        let edited = File.ReadAllText(mainPath).Replace("v1", "v2")
        let body =
            Json.serialize (ofList [ Keyword "path"; Obj(mainPath :> obj); Keyword "content"; Obj(edited :> obj) ])
        Assert.Equal(200, client.Post "/file" body)
        client.AwaitEvent "\"type\":\"reloaded\"" |> ignore
        client.Post "/message" """{"text":"hi again"}""" |> ignore
        client.AwaitEvent "\"marker\":\"v2\"" |> ignore
        client.AwaitEvent "\"type\":\"done\"" |> ignore
        // Run the agent's suite from the UI.
        Assert.Equal(202, client.Post "/test" "{}")
        let testDone = client.AwaitEvent "\"type\":\"test-done\""
        Assert.Contains("\"passed\":1", testDone)
        // Foreign paths are refused.
        Assert.Equal(403, client.Post "/file" """{"path":"/etc/hosts","content":"nope"}""")
    finally
        server.stop ()
        Directory.Delete(root, true)
        Directory.Delete(agentDir, true)

[<Fact>]
let ``settings report key status, accept keys, and switch models`` () =
    let root = newRoot "jern-uiset-"
    let makeBridge _ _ : AnthropicBridge.LlmBridge =
        fun _ -> Choice1Of2 (IronKernel.Ast.Default "unused")
    let server = startServer root makeBridge [] None None
    try
        use client = new UiClient(server)
        // groq's key env is safely unset in test environments.
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null)
        let before = client.Get("/settings")
        Assert.Contains("\"name\":\"groq\",\"key_env\":\"GROQ_API_KEY\",\"has_key\":false", before)
        // Session-only key: env var set in-process, never echoed back.
        Assert.Equal(200, client.Post "/settings/key" """{"provider":"groq","key":"gk-test-123"}""")
        let after = client.Get("/settings")
        Assert.Contains("\"name\":\"groq\",\"key_env\":\"GROQ_API_KEY\",\"has_key\":true", after)
        Assert.DoesNotContain("gk-test-123", after)
        Assert.Equal("gk-test-123", Environment.GetEnvironmentVariable "GROQ_API_KEY")
        // Model switching validates the spec and rebuilds the session.
        Assert.Equal(400, client.Post "/settings/model" """{"model":"nonsense"}""")
        Assert.Equal(200, client.Post "/settings/model" """{"model":"ollama/qwen3"}""")
        client.AwaitEvent "\"model\":\"ollama/qwen3\"" |> ignore
    finally
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null)
        server.stop ()
        Directory.Delete(root, true)

/// --auto and "always allow": auto-approved actions emit a note instead of
/// a card, and an "always" answer stops cards for that tool.
[<Fact>]
let ``auto mode and always answers replace approval cards`` () =
    let root = newRoot "jern-uiauto-"
    File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
    File.WriteAllText(Path.Combine(root, "b.txt"), "one\n")
    let mutable turn = 0
    let makeBridge _ (_: string -> unit) : AnthropicBridge.LlmBridge =
        fun _ ->
            turn <- turn + 1
            match turn with
            | 1 ->
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"x1","name":"edit_file","input":{"path":"a.txt","old_string":"one","new_string":"two"}}]}""")
            | 2 ->
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"first done"}]}""")
            | 3 ->
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"x2","name":"edit_file","input":{"path":"b.txt","old_string":"one","new_string":"two"}}]}""")
            | _ ->
                Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"second done"}]}""")
    let server =
        startServer root makeBridge (Session.agentPackageSources (repoAgentDir ())) None (Some "test/model")
    try
        use client = new UiClient(server)
        client.AwaitEvent "\"type\":\"state\"" |> ignore
        // Answer the first card with always=true…
        client.Post "/message" """{"text":"edit a"}""" |> ignore
        let approval = client.AwaitEvent "\"type\":\"approval\""
        let id =
            match Tools.plistTryGet "id" (Json.deserialize approval) with
            | Some (Obj (:? string as s)) -> s
            | _ -> failwith "no id"
        client.Post "/approve" (sprintf """{"id":"%s","approved":true,"always":true}""" id) |> ignore
        client.AwaitEvent "\"type\":\"done\"" |> ignore
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(root, "a.txt")))
        // …and the second edit_file goes through with a note, no card.
        client.Post "/message" """{"text":"edit b"}""" |> ignore
        client.AwaitEvent "\"type\":\"auto-approved\"" |> ignore
        client.AwaitEvent "\"type\":\"done\"" |> ignore
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(root, "b.txt")))
        // The live toggle exists too.
        Assert.Equal(200, client.Post "/settings/auto" """{"on":true}""")
        client.AwaitEvent "\"auto\":true" |> ignore
    finally
        server.stop ()
        Directory.Delete(root, true)
