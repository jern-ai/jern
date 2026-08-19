namespace Jern.Cli

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

/// `jern ui` — the chat session as a local web app, served by the binary
/// itself on 127.0.0.1. One page (ui/index.html, a file you can edit like
/// everything else), Server-Sent Events out, a handful of POSTs in.
///
///   GET  /?token=…        the app (the token comes from the printed URL)
///   GET  /events          SSE: text, trace, approval, state, done, error,
///                         reloaded, test-result, test-done
///   GET  /state           model, session id, workspace, version
///   GET  /files           the brain: loaded agent sources + workspace policy
///   GET  /file?path=…     read one of those files
///   POST /file            {"path","content"} — save and rebuild the session
///   POST /test            run the agent package's test suite (replay)
///   GET  /settings        providers, key status (never values), model
///   POST /settings/key    {"provider","key","persist"?} — set an API key
///   POST /settings/model  {"model"} — switch model, rebuild the session
///   POST /message         {"text"} — one turn at a time (409 when busy)
///   POST /approve         {"id","approved"}
///   POST /interrupt       abort the current turn at the next dispatch
///   POST /undo            revert the last jern-authored commit
///
/// Every request must carry the startup token (query `token` or the
/// `X-Jern-Token` header) — the Jupyter pattern, so other local processes
/// cannot drive the session, edit the brain, or set keys.
///
/// The HTTP layer is a deliberately small TcpListener server (HTTP/1.1,
/// Connection: close per API call; event streams stay open). The managed
/// HttpListener mis-reads request bodies on kept-alive connections, and a
/// localhost UI needs none of its features.
module Ui =

    type Config =
        { root: string
          /// Given the model override and the streaming text sink, the bridge.
          makeBridge: string option -> (string -> unit) -> AnthropicBridge.LlmBridge
          /// Provider table for the settings panel and model validation.
          providers: Providers.Config
          /// Initial model override (CLI --model), switchable in the UI.
          model: string option
          /// The agent package directory (its test suite runs from here).
          agentDir: string option
          agentSources: string list
          agentConfig: LispVal
          mcpServers: Mcp.ServerSpec list
          budget: LispVal
          /// 0 picks a free port.
          port: int }

    type Server =
        { url: string
          token: string
          /// Blocks until the listener stops (ctrl-c kills the process).
          run: unit -> unit
          stop: unit -> unit }

    // --- a tiny HTTP/1.1 request/response layer -----------------------------

    type private Request =
        { verb: string
          path: string
          query: Map<string, string>
          headers: Map<string, string>
          body: string }

    /// Read one header line (bytes up to CRLF) without buffering past it.
    let private readLine (stream: Stream) =
        let builder = StringBuilder()
        let mutable go = true
        while go do
            match stream.ReadByte() with
            | -1 -> go <- false
            | 10 (* \n *) -> go <- false
            | 13 (* \r *) -> ()
            | b -> builder.Append(char b) |> ignore
        builder.ToString()

    let private parseQuery (raw: string) =
        raw.Split('&')
        |> Array.choose (fun pair ->
            match pair.IndexOf '=' with
            | -1 -> None
            | i -> Some(Uri.UnescapeDataString(pair.Substring(0, i)),
                        Uri.UnescapeDataString(pair.Substring(i + 1))))
        |> Map.ofArray

    let private readRequest (stream: Stream) : Request option =
        match readLine stream with
        | "" -> None
        | requestLine ->
            let parts = requestLine.Split(' ')
            if parts.Length < 2 then None
            else
                let target = parts.[1]
                let path, query =
                    match target.IndexOf '?' with
                    | -1 -> target, Map.empty
                    | i -> target.Substring(0, i), parseQuery (target.Substring(i + 1))
                let headers = System.Collections.Generic.Dictionary<string, string>()
                let mutable line = readLine stream
                while line <> "" do
                    let i = line.IndexOf ':'
                    if i > 0 then
                        headers.[line.Substring(0, i).Trim().ToLowerInvariant()] <-
                            line.Substring(i + 1).Trim()
                    line <- readLine stream
                let contentLength =
                    match headers.TryGetValue "content-length" with
                    | true, v -> (match Int32.TryParse v with true, n -> n | _ -> 0)
                    | _ -> 0
                let body =
                    if contentLength <= 0 then ""
                    else
                        let buffer = Array.zeroCreate contentLength
                        let mutable filled = 0
                        let mutable more = true
                        while more && filled < contentLength do
                            let n = stream.Read(buffer, filled, contentLength - filled)
                            if n = 0 then more <- false else filled <- filled + n
                        Encoding.UTF8.GetString(buffer, 0, filled)
                Some { verb = parts.[0]
                       path = path
                       query = query
                       headers = headers |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                       body = body }

    let private statusText = function
        | 200 -> "OK" | 202 -> "Accepted" | 400 -> "Bad Request" | 403 -> "Forbidden"
        | 404 -> "Not Found" | 409 -> "Conflict" | code -> string code

    let private respond (stream: Stream) (status: int) (contentType: string) (body: string) =
        let bytes = Encoding.UTF8.GetBytes(body: string)
        let head =
            sprintf "HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %d\r\nConnection: close\r\n\r\n"
                status (statusText status) contentType bytes.Length
        let headBytes = Encoding.UTF8.GetBytes head
        stream.Write(headBytes, 0, headBytes.Length)
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush()

    let private sseHeader =
        "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n"
        |> Encoding.UTF8.GetBytes

    // -------------------------------------------------------------------------

    let private jsonEvent (pairs: (string * LispVal) list) =
        pairs
        |> List.collect (fun (k, v) -> [ Keyword k; v ])
        |> ofList
        |> Json.serialize

    let private str (s: string) = Obj(s :> obj)

    let start (config: Config) : Server =
        let listener = new TcpListener(IPAddress.Loopback, config.port)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        let token = Guid.NewGuid().ToString "N"
        let url = sprintf "http://127.0.0.1:%d/?token=%s" port token

        // --- SSE fan-out -----------------------------------------------------
        let clients = ConcurrentDictionary<Guid, Stream>()
        let broadcast (json: string) =
            let payload = Encoding.UTF8.GetBytes("data: " + json + "\n\n")
            for kv in clients do
                try
                    kv.Value.Write(payload, 0, payload.Length)
                    kv.Value.Flush()
                with _ ->
                    clients.TryRemove kv.Key |> ignore

        // --- session ---------------------------------------------------------
        let sessionId = SessionStore.newId ()
        let interrupted = ref false
        let inputTokens = ref 0L
        let outputTokens = ref 0L
        let currentModel = ref config.model

        let pendingApprovals = ConcurrentDictionary<string, TaskCompletionSource<bool>>()
        let approver (description: string) =
            let id = Guid.NewGuid().ToString "N"
            let tcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            pendingApprovals.[id] <- tcs
            broadcast (jsonEvent [ "type", str "approval"; "id", str id; "description", str description ])
            tcs.Task.Result

        let onText piece =
            if interrupted.Value then raise Interrupted
            broadcast (jsonEvent [ "type", str "text"; "text", str piece ])

        let metered (inner: AnthropicBridge.LlmBridge) : AnthropicBridge.LlmBridge =
            fun request ->
                let result = inner request
                match result with
                | Choice2Of2 response ->
                    match Tools.plistTryGet "usage" response with
                    | Some usage ->
                        let grab key =
                            match Tools.plistTryGet key usage with
                            | Some (Obj v) -> (try Convert.ToInt64 v with _ -> 0L)
                            | _ -> 0L
                        Interlocked.Add(inputTokens, grab "input_tokens") |> ignore
                        Interlocked.Add(outputTokens, grab "output_tokens") |> ignore
                    | None -> ()
                | Choice1Of2 _ -> ()
                result

        let traceSink (line: string) =
            broadcast (sprintf """{"type":"trace","event":%s}""" line)

        let buildSession () =
            Session.createWith
                { Session.configIn config.root (metered (config.makeBridge currentModel.Value onText)) with
                    traceSink = Some traceSink
                    agentSources = config.agentSources
                    approver = Some approver
                    agentConfig = config.agentConfig
                    mcpServers = config.mcpServers
                    budget = config.budget
                    interrupted = fun () -> interrupted.Value }

        let session =
            match buildSession () with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> ref s

        let messages = ref Nil
        let turnRunning = ref 0
        let testRunning = ref 0

        let modelLabel () =
            match currentModel.Value with
            | Some m -> m
            | None -> config.providers.defaultModel

        let stateJson () =
            jsonEvent
                [ "type", str "state"
                  "model", str (modelLabel ())
                  "session", str sessionId
                  "root", str config.root
                  "version", str AgentEnv.version
                  "input_tokens", Obj(inputTokens.Value :> obj)
                  "output_tokens", Obj(outputTokens.Value :> obj) ]

        /// Rebuild the session (after a brain edit or model switch),
        /// keeping the conversation — history is data, not session state.
        let reload () : Result<unit, string> =
            if turnRunning.Value <> 0 then Error "a turn is running; try again when it finishes"
            else
                match buildSession () with
                | Choice1Of2 error -> Error(showError error)
                | Choice2Of2 fresh ->
                    session.Value <- fresh
                    Ok()

        let runTurn (text: string) =
            interrupted.Value <- false
            match Session.runChatTurn session.Value messages.Value text with
            | Choice1Of2 error ->
                broadcast (jsonEvent [ "type", str "error"; "message", str (showError error) ])
            | Choice2Of2 updated ->
                messages.Value <- updated
                SessionStore.save config.root sessionId updated
            broadcast (stateJson ())
            broadcast (jsonEvent [ "type", str "done" ])
            Interlocked.Exchange(turnRunning, 0) |> ignore

        // --- the brain's files ------------------------------------------------
        let policyPath = Path.GetFullPath(Path.Combine(config.root, ".jern", "policy.ikr"))
        let installedRoot = Path.GetFullPath AppContext.BaseDirectory

        /// name, absolute path, editable, exists. Installed files (beside the
        /// binary) are read-only from the UI: edit a workspace copy instead.
        let editableFiles () =
            let agentFiles =
                config.agentSources
                |> List.map (fun p ->
                    let full = Path.GetFullPath p
                    let editable = not (full.StartsWith(installedRoot, StringComparison.Ordinal))
                    Path.GetFileName full, full, editable, File.Exists full)
            agentFiles @ [ ".jern/policy.ikr", policyPath, true, File.Exists policyPath ]

        let fileAllowed (path: string) =
            editableFiles () |> List.exists (fun (_, full, _, _) -> full = Path.GetFullPath path)

        let filesJson () =
            let entries =
                editableFiles ()
                |> List.map (fun (name, full, editable, exists) ->
                    ofList
                        [ Keyword "name"; str name
                          Keyword "path"; str full
                          Keyword "editable"; Bool editable
                          Keyword "exists"; Bool exists ])
            jsonEvent
                [ "type", str "files"
                  "files", Vector(Array.ofList entries)
                  "policy_template", str Session.policyTemplate ]

        let runTests () =
            match config.agentDir with
            | None ->
                broadcast (jsonEvent [ "type", str "test-done"; "error", str "no agent package directory for this session" ])
            | Some dir ->
                let outcome =
                    match TestRunner.run dir Fixtures.Replay with
                    | Error message ->
                        jsonEvent [ "type", str "test-done"; "error", str message ]
                    | Ok summary ->
                        for o in summary.outcomes do
                            broadcast
                                (jsonEvent
                                    [ "type", str "test-result"
                                      "name", str o.name
                                      "ok", Bool o.error.IsNone
                                      "error", str (defaultArg o.error "") ])
                        jsonEvent
                            [ "type", str "test-done"
                              "passed", Obj(summary.Passed.Length :> obj)
                              "failed", Obj(summary.Failed.Length :> obj) ]
                broadcast outcome
                Interlocked.Exchange(testRunning, 0) |> ignore

        // --- settings ----------------------------------------------------------
        let settingsJson () =
            let providerEntries =
                config.providers.providers
                |> Map.toList
                |> List.map (fun (name, p) ->
                    let hasKey =
                        match p.apiKeyEnv with
                        | None -> true // local endpoints need no key
                        | Some env -> not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable env))
                    ofList
                        [ Keyword "name"; str name
                          Keyword "key_env"; (match p.apiKeyEnv with Some e -> str e | None -> Keyword "null")
                          Keyword "has_key"; Bool hasKey ])
            let aliases =
                config.providers.aliases |> Map.toList |> List.map (fst >> str)
            jsonEvent
                [ "type", str "settings"
                  "model", str (modelLabel ())
                  "providers", Vector(Array.ofList providerEntries)
                  "aliases", Vector(Array.ofList aliases) ]

        // --- routing ----------------------------------------------------------
        let bodyField (request: Request) key =
            let parsed = try Some(Json.deserialize request.body) with _ -> None
            match parsed |> Option.bind (Tools.plistTryGet key) with
            | Some (Obj (:? string as s)) -> Some s
            | Some (Bool b) -> Some(if b then "true" else "false")
            | _ -> None

        let indexPath =
            let local = Path.Combine("ui", "index.html")
            let installed = Path.Combine(AppContext.BaseDirectory, "ui", "index.html")
            if File.Exists local then local else installed

        let authorized (request: Request) =
            match Map.tryFind "token" request.query with
            | Some t when t = token -> true
            | _ ->
                match Map.tryFind "x-jern-token" request.headers with
                | Some t when t = token -> true
                | _ -> false

        /// Handles one connection: one request, one response — except
        /// /events, which keeps the socket for the event stream.
        let handleClient (client: TcpClient) =
            let stream = client.GetStream() :> Stream
            let mutable keepOpen = false
            try
                match readRequest stream with
                | None -> ()
                | Some request when not (authorized request) ->
                    respond stream 403 "text/plain"
                        "missing or wrong token — open the exact URL jern printed at startup"
                | Some request ->
                    match request.verb, request.path with
                    | "GET", "/" ->
                        respond stream 200 "text/html; charset=utf-8" (File.ReadAllText indexPath)
                    | "GET", "/state" ->
                        respond stream 200 "application/json" (stateJson ())
                    | "GET", "/events" ->
                        stream.Write(sseHeader, 0, sseHeader.Length)
                        // Register before the hello event: a client that has
                        // received state is guaranteed to be subscribed.
                        clients.[Guid.NewGuid()] <- stream
                        let hello = Encoding.UTF8.GetBytes("data: " + stateJson () + "\n\n")
                        stream.Write(hello, 0, hello.Length)
                        stream.Flush()
                        keepOpen <- true
                    | "GET", "/files" ->
                        respond stream 200 "application/json" (filesJson ())
                    | "GET", "/file" ->
                        (match Map.tryFind "path" request.query with
                         | Some path when fileAllowed path && File.Exists path ->
                             respond stream 200 "application/json"
                                 (jsonEvent [ "type", str "file"; "path", str path
                                              "content", str (File.ReadAllText path) ])
                         | Some path when fileAllowed path ->
                             respond stream 200 "application/json"
                                 (jsonEvent [ "type", str "file"; "path", str path; "content", str "" ])
                         | _ -> respond stream 403 "application/json" """{"error":"not an editable file of this session"}""")
                    | "POST", "/file" ->
                        (match bodyField request "path", bodyField request "content" with
                         | Some path, Some content when fileAllowed path ->
                             let editable =
                                 editableFiles ()
                                 |> List.exists (fun (_, full, e, _) -> full = Path.GetFullPath path && e)
                             if not editable then
                                 respond stream 403 "application/json"
                                     """{"error":"this file is installed beside the binary; eject the agent to edit a workspace copy"}"""
                             elif turnRunning.Value <> 0 then
                                 respond stream 409 "application/json" """{"error":"a turn is running; save when it finishes"}"""
                             else
                                 Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                                 File.WriteAllText(path, content)
                                 match reload () with
                                 | Ok () ->
                                     broadcast (jsonEvent [ "type", str "reloaded"; "path", str (Path.GetFileName path) ])
                                     respond stream 200 "application/json" """{"ok":true}"""
                                 | Error message ->
                                     // The file is saved but does not load; the
                                     // old session keeps running.
                                     respond stream 400 "application/json"
                                         (jsonEvent [ "error", str ("saved, but reload failed: " + message) ])
                         | _ -> respond stream 403 "application/json" """{"error":"not an editable file of this session"}""")
                    | "POST", "/test" ->
                        if Interlocked.CompareExchange(testRunning, 1, 0) = 0 then
                            Task.Run(runTests) |> ignore
                            respond stream 202 "application/json" """{"ok":true}"""
                        else
                            respond stream 409 "application/json" """{"error":"tests are already running"}"""
                    | "GET", "/settings" ->
                        respond stream 200 "application/json" (settingsJson ())
                    | "POST", "/settings/key" ->
                        (match bodyField request "provider", bodyField request "key" with
                         | Some providerName, Some key when key.Trim() <> "" ->
                             (match Map.tryFind providerName config.providers.providers with
                              | Some p ->
                                  (match p.apiKeyEnv with
                                   | Some env ->
                                       if bodyField request "persist" = Some "true" then
                                           Providers.saveCredential env (key.Trim())
                                       else
                                           Environment.SetEnvironmentVariable(env, key.Trim())
                                       respond stream 200 "application/json" (settingsJson ())
                                   | None ->
                                       respond stream 400 "application/json" """{"error":"this provider needs no key"}""")
                              | None -> respond stream 404 "application/json" """{"error":"unknown provider"}""")
                         | _ -> respond stream 400 "application/json" """{"error":"body must be {\"provider\":\"...\",\"key\":\"...\"}"}""")
                    | "POST", "/settings/model" ->
                        (match bodyField request "model" with
                         | Some spec ->
                             (match Providers.resolve config.providers (spec.Trim()) with
                              | Error message ->
                                  respond stream 400 "application/json" (jsonEvent [ "error", str message ])
                              | Ok _ ->
                                  currentModel.Value <- Some(spec.Trim())
                                  match reload () with
                                  | Ok () ->
                                      broadcast (stateJson ())
                                      respond stream 200 "application/json" (stateJson ())
                                  | Error message ->
                                      respond stream 409 "application/json" (jsonEvent [ "error", str message ]))
                         | None -> respond stream 400 "application/json" """{"error":"body must be {\"model\":\"provider/model\"}"}""")
                    | "POST", "/message" ->
                        (match bodyField request "text" with
                         | Some text when text.Trim() <> "" ->
                             if Interlocked.CompareExchange(turnRunning, 1, 0) = 0 then
                                 Task.Run(fun () -> runTurn text) |> ignore
                                 respond stream 202 "application/json" """{"ok":true}"""
                             else
                                 respond stream 409 "application/json" """{"error":"a turn is already running"}"""
                         | _ -> respond stream 400 "application/json" """{"error":"body must be {\"text\":\"...\"}"}""")
                    | "POST", "/approve" ->
                        (match bodyField request "id", bodyField request "approved" with
                         | Some id, Some answer ->
                             (match pendingApprovals.TryRemove id with
                              | true, tcs ->
                                  tcs.TrySetResult((answer = "true")) |> ignore
                                  respond stream 200 "application/json" """{"ok":true}"""
                              | _ -> respond stream 404 "application/json" """{"error":"no such approval"}""")
                         | _ -> respond stream 400 "application/json" """{"error":"body must be {\"id\":\"...\",\"approved\":bool}"}""")
                    | "POST", "/interrupt" ->
                        interrupted.Value <- true
                        respond stream 200 "application/json" """{"ok":true}"""
                    | "POST", "/undo" ->
                        (match Git.undoLast config.root with
                         | Ok subject ->
                             respond stream 200 "application/json" (jsonEvent [ "type", str "undone"; "subject", str subject ])
                         | Error message ->
                             respond stream 409 "application/json" (jsonEvent [ "type", str "error"; "message", str message ]))
                    | _ -> respond stream 404 "text/plain" "not found"
            with _ -> ()
            if not keepOpen then
                try client.Close() with _ -> ()

        let running = ref true
        let acceptLoop () =
            while running.Value do
                try
                    let client = listener.AcceptTcpClient()
                    Task.Run(fun () -> handleClient client) |> ignore
                with _ -> ()

        { url = url
          token = token
          run = acceptLoop
          stop =
            fun () ->
                running.Value <- false
                try listener.Stop() with _ -> () }
