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
/// everything else), Server-Sent Events out, three POSTs in:
///
///   GET  /            the app
///   GET  /events      SSE: text, trace, approval, done, error, state
///   GET  /state       model, session id, workspace, version
///   POST /message     {"text": "…"} — one turn at a time (409 when busy)
///   POST /approve     {"id": "…", "approved": true|false}
///   POST /interrupt   abort the current turn at the next dispatch
///   POST /undo        revert the last jern-authored commit
///
/// Approvals become interactive cards: the policy handler's jern/approve
/// blocks the turn until the browser answers.
///
/// The HTTP layer is a deliberately small TcpListener server (HTTP/1.1,
/// Connection: close per API call; event streams stay open). The managed
/// HttpListener mis-reads request bodies on kept-alive connections, and a
/// localhost UI needs none of its features.
module Ui =

    type Config =
        { root: string
          /// Given the streaming text sink, the routed LLM bridge.
          makeBridge: (string -> unit) -> AnthropicBridge.LlmBridge
          agentSources: string list
          agentConfig: LispVal
          mcpServers: Mcp.ServerSpec list
          budget: LispVal
          modelLabel: string
          /// 0 picks a free port.
          port: int }

    type Server =
        { url: string
          /// Blocks until the listener stops (ctrl-c kills the process).
          run: unit -> unit
          stop: unit -> unit }

    // --- a tiny HTTP/1.1 request/response layer -----------------------------

    type private Request =
        { verb: string
          path: string
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

    let private readRequest (stream: Stream) : Request option =
        match readLine stream with
        | "" -> None
        | requestLine ->
            let parts = requestLine.Split(' ')
            if parts.Length < 2 then None
            else
                let mutable contentLength = 0
                let mutable line = readLine stream
                while line <> "" do
                    let i = line.IndexOf ':'
                    if i > 0 && line.Substring(0, i).Trim().ToLowerInvariant() = "content-length" then
                        Int32.TryParse(line.Substring(i + 1).Trim(), &contentLength) |> ignore
                    line <- readLine stream
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
                Some { verb = parts.[0]; path = parts.[1].Split('?').[0]; body = body }

    let private statusText = function
        | 200 -> "OK" | 202 -> "Accepted" | 400 -> "Bad Request"
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
        let url = sprintf "http://127.0.0.1:%d/" port

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

        let meteredBridge: AnthropicBridge.LlmBridge =
            let inner = config.makeBridge onText
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

        let session =
            match Session.createWith
                    { Session.configIn config.root meteredBridge with
                        traceSink = Some traceSink
                        agentSources = config.agentSources
                        approver = Some approver
                        agentConfig = config.agentConfig
                        mcpServers = config.mcpServers
                        budget = config.budget
                        interrupted = fun () -> interrupted.Value } with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s

        let messages = ref Nil
        let turnRunning = ref 0

        let stateJson () =
            jsonEvent
                [ "type", str "state"
                  "model", str config.modelLabel
                  "session", str sessionId
                  "root", str config.root
                  "version", str AgentEnv.version
                  "input_tokens", Obj(inputTokens.Value :> obj)
                  "output_tokens", Obj(outputTokens.Value :> obj) ]

        let runTurn (text: string) =
            interrupted.Value <- false
            match Session.runChatTurn session messages.Value text with
            | Choice1Of2 error ->
                broadcast (jsonEvent [ "type", str "error"; "message", str (showError error) ])
            | Choice2Of2 updated ->
                messages.Value <- updated
                SessionStore.save config.root sessionId updated
            broadcast (stateJson ())
            broadcast (jsonEvent [ "type", str "done" ])
            Interlocked.Exchange(turnRunning, 0) |> ignore

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

        /// Handles one connection: one request, one response — except
        /// /events, which keeps the socket for the event stream.
        let handleClient (client: TcpClient) =
            let stream = client.GetStream() :> Stream
            let mutable keepOpen = false
            try
                match readRequest stream with
                | None -> ()
                | Some request ->
                    match request.verb, request.path with
                    | "GET", "/" ->
                        respond stream 200 "text/html; charset=utf-8" (File.ReadAllText indexPath)
                    | "GET", "/state" ->
                        respond stream 200 "application/json" (stateJson ())
                    | "GET", "/events" ->
                        stream.Write(sseHeader, 0, sseHeader.Length)
                        let hello = Encoding.UTF8.GetBytes("data: " + stateJson () + "\n\n")
                        stream.Write(hello, 0, hello.Length)
                        stream.Flush()
                        clients.[Guid.NewGuid()] <- stream
                        keepOpen <- true
                    | "POST", "/message" ->
                        match bodyField request "text" with
                        | Some text when text.Trim() <> "" ->
                            if Interlocked.CompareExchange(turnRunning, 1, 0) = 0 then
                                Task.Run(fun () -> runTurn text) |> ignore
                                respond stream 202 "application/json" """{"ok":true}"""
                            else
                                respond stream 409 "application/json" """{"error":"a turn is already running"}"""
                        | _ ->
                            respond stream 400 "application/json" """{"error":"body must be {\"text\":\"...\"}"}"""
                    | "POST", "/approve" ->
                        match bodyField request "id", bodyField request "approved" with
                        | Some id, Some answer ->
                            match pendingApprovals.TryRemove id with
                            | true, tcs ->
                                tcs.TrySetResult((answer = "true")) |> ignore
                                respond stream 200 "application/json" """{"ok":true}"""
                            | _ -> respond stream 404 "application/json" """{"error":"no such approval"}"""
                        | _ ->
                            respond stream 400 "application/json" """{"error":"body must be {\"id\":\"...\",\"approved\":bool}"}"""
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
          run = acceptLoop
          stop =
            fun () ->
                running.Value <- false
                try listener.Stop() with _ -> () }
