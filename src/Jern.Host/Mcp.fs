namespace Jern.Host

open System
open System.Diagnostics
open IronKernel.Ast
open IronKernel.Errors

/// A minimal MCP (Model Context Protocol) client, stdio transport.
///
/// The protocol slice jern needs — initialize, tools/list, tools/call over
/// newline-delimited JSON-RPC 2.0 — is small enough to implement directly on
/// the frozen JSON⇄Kernel convention (Json.fs), so MCP tools cross the
/// boundary as the same keyword plists as everything else and no wire shape
/// is hidden behind a dependency.
///
/// Servers come from jern.json `mcp_servers`; their tools register in the
/// agent's tool registry as `mcp__<server>__<tool>` and dispatch through the
/// ordinary jern/tool-call effect, so the policy, approval, trace, and
/// fixture layers all apply to them unchanged. The default policy asks
/// before every MCP call (unknown tools are :ask).
module Mcp =

    type ServerSpec =
        { name: string
          command: string
          args: string list
          env: (string * string) list }

    type Server =
        { spec: ServerSpec
          proc: Process
          sync: obj
          mutable nextId: int
          mutable dead: string option }

    let private handshakeTimeout = TimeSpan.FromSeconds 30.0
    let private callTimeout = TimeSpan.FromSeconds 120.0
    let protocolVersion = "2025-06-18"

    /// Every server started this process-lifetime, killed at exit.
    let private live = ResizeArray<Server>()
    let private liveSync = obj ()

    let private killServer (server: Server) =
        try
            if not server.proc.HasExited then server.proc.Kill(true)
        with _ -> ()

    do AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
        lock liveSync (fun () -> live |> Seq.iter killServer))

    let private plistGet key value = Tools.plistTryGet key value

    let private str = function
        | Some (Obj (:? string as s)) -> Some s
        | _ -> None

    let private sendLine (server: Server) (message: LispVal) =
        server.proc.StandardInput.WriteLine(Json.serialize message)
        server.proc.StandardInput.Flush()

    let private readLine (server: Server) (timeout: TimeSpan) : Result<string, string> =
        let task = server.proc.StandardOutput.ReadLineAsync()
        if task.Wait timeout then
            match task.Result with
            | null -> Error "server closed its stdout"
            | line -> Ok line
        else
            Error(sprintf "no response within %.0f seconds" timeout.TotalSeconds)

    /// Send a request and read frames until its response arrives.
    /// Server-initiated requests get a method-not-found error; notifications
    /// are ignored. Any transport failure marks the server dead.
    let private request (server: Server) (timeout: TimeSpan) (method: string) (parameters: LispVal option)
                        : Result<LispVal, string> =
        lock server.sync (fun () ->
            match server.dead with
            | Some reason -> Error(sprintf "server '%s' is unavailable: %s" server.spec.name reason)
            | None ->
                let id = server.nextId
                server.nextId <- id + 1
                let message =
                    ofList
                        ([ Keyword "jsonrpc"; Obj("2.0" :> obj)
                           Keyword "id"; Obj(id :> obj)
                           Keyword "method"; Obj(method :> obj) ]
                         @ (match parameters with
                            | Some p -> [ Keyword "params"; p ]
                            | None -> []))
                let fail reason =
                    server.dead <- Some reason
                    killServer server
                    Error(sprintf "MCP server '%s': %s" server.spec.name reason)
                try
                    sendLine server message
                    let rec await () =
                        match readLine server timeout with
                        | Error reason -> fail reason
                        | Ok line ->
                            let frame =
                                try Some(Json.deserialize line)
                                with _ -> None
                            match frame with
                            | None -> await () // not JSON (a stray log line); skip
                            | Some frame ->
                                let frameId = plistGet "id" frame
                                let isMine =
                                    match frameId with
                                    | Some (Obj v) -> (try Convert.ToInt32 v = id with _ -> false)
                                    | _ -> false
                                if isMine then
                                    match plistGet "result" frame, plistGet "error" frame with
                                    | Some result, _ -> Ok result
                                    | _, Some error ->
                                        let text =
                                            match str (plistGet "message" (error)) with
                                            | Some m -> m
                                            | None -> Json.serialize error
                                        Error(sprintf "MCP server '%s': %s" server.spec.name text)
                                    | _ -> fail "response carries neither result nor error"
                                elif (plistGet "method" frame).IsSome && frameId.IsSome then
                                    // A server->client request we don't support.
                                    sendLine server
                                        (ofList
                                            [ Keyword "jsonrpc"; Obj("2.0" :> obj)
                                              Keyword "id"; frameId.Value
                                              Keyword "error"
                                              ofList [ Keyword "code"; Obj(-32601 :> obj)
                                                       Keyword "message"; Obj("method not supported by jern" :> obj) ] ])
                                    await ()
                                else
                                    await () // a notification or an unrelated frame
                    await ()
                with ex -> fail ex.Message)

    let private notify (server: Server) (method: string) =
        sendLine server
            (ofList [ Keyword "jsonrpc"; Obj("2.0" :> obj); Keyword "method"; Obj(method :> obj) ])

    /// Start the server process and run the initialize handshake.
    let connect (spec: ServerSpec) : Result<Server, string> =
        let proc = new Process()
        proc.StartInfo.FileName <- spec.command
        spec.args |> List.iter proc.StartInfo.ArgumentList.Add
        spec.env |> List.iter (fun (k, v) -> proc.StartInfo.Environment.[k] <- v)
        proc.StartInfo.RedirectStandardInput <- true
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        proc.StartInfo.UseShellExecute <- false
        try
            if not (proc.Start()) then
                Error(sprintf "MCP server '%s': failed to start '%s'" spec.name spec.command)
            else
                // Keep stderr drained so a chatty server can't fill the pipe.
                proc.ErrorDataReceived.Add(fun _ -> ())
                proc.BeginErrorReadLine()
                let server = { spec = spec; proc = proc; sync = obj (); nextId = 1; dead = None }
                lock liveSync (fun () -> live.Add server)
                let initParams =
                    ofList
                        [ Keyword "protocolVersion"; Obj(protocolVersion :> obj)
                          Keyword "capabilities"; Nil
                          Keyword "clientInfo"
                          ofList [ Keyword "name"; Obj("jern" :> obj)
                                   Keyword "version"; Obj(AgentEnv.version :> obj) ] ]
                match request server handshakeTimeout "initialize" (Some initParams) with
                | Error reason -> Error reason
                | Ok _ ->
                    notify server "notifications/initialized"
                    Ok server
        with ex ->
            Error(sprintf "MCP server '%s': %s" spec.name ex.Message)

    let shutdown (server: Server) =
        killServer server
        lock liveSync (fun () -> live.Remove server |> ignore)

    /// The server's tools as jern registry descriptors:
    /// (:name "mcp__<server>__<tool>" :description "…" :input_schema <schema>).
    let listTools (server: Server) : Result<LispVal list, string> =
        let rec page (cursor: string option) (acc: LispVal list) =
            let parameters =
                cursor |> Option.map (fun c -> ofList [ Keyword "cursor"; Obj(c :> obj) ])
            match request server handshakeTimeout "tools/list" parameters with
            | Error reason -> Error reason
            | Ok result ->
                let tools =
                    match plistGet "tools" result with
                    | Some (Vector items) -> Array.toList items
                    | _ -> []
                let descriptors =
                    tools
                    |> List.choose (fun tool ->
                        match str (plistGet "name" tool) with
                        | None -> None
                        | Some name ->
                            let description =
                                match str (plistGet "description" tool) with
                                | Some d -> d
                                | None -> ""
                            let schema =
                                match plistGet "inputSchema" tool with
                                | Some s -> s
                                | None -> ofList [ Keyword "type"; Obj("object" :> obj) ]
                            Some(ofList
                                    [ Keyword "name"
                                      Obj(sprintf "mcp__%s__%s" server.spec.name name :> obj)
                                      Keyword "description"; Obj(description :> obj)
                                      Keyword "input_schema"; schema ]))
                match str (plistGet "nextCursor" result) with
                | Some next when next <> "" -> page (Some next) (acc @ descriptors)
                | _ -> Ok(acc @ descriptors)
        page None []

    let private toolResult (isError: bool) (content: string) =
        ofList [ Keyword "content"; Obj(content :> obj); Keyword "is_error"; Bool isError ]

    /// Call `tool` (the server's own name for it) with a plist of arguments;
    /// returns a jern tool-result plist. Protocol errors are error results
    /// the model can react to, never Kernel errors.
    let callTool (server: Server) (tool: string) (arguments: LispVal) : LispVal =
        let parameters =
            ofList [ Keyword "name"; Obj(tool :> obj); Keyword "arguments"; arguments ]
        match request server callTimeout "tools/call" (Some parameters) with
        | Error reason -> toolResult true reason
        | Ok result ->
            let isError =
                match plistGet "isError" result with
                | Some (Bool b) -> b
                | _ -> false
            let text =
                match plistGet "content" result with
                | Some (Vector blocks) ->
                    blocks
                    |> Array.map (fun block ->
                        match str (plistGet "text" block) with
                        | Some t -> t
                        | None ->
                            match str (plistGet "type" block) with
                            | Some kind -> sprintf "[%s content elided]" kind
                            | None -> "[non-text content elided]")
                    |> String.concat "\n"
                | _ -> Json.serialize result
            toolResult isError (if text = "" then "(no content)" else text)

    /// Dispatch a jern/tool-call payload whose :name is `mcp__<server>__<tool>`.
    let dispatch (servers: Map<string, Server>) (call: LispVal) : ThrowsError<LispVal> =
        match str (plistGet "name" call) with
        | Some name when name.StartsWith "mcp__" ->
            let rest = name.Substring 5
            match rest.IndexOf "__" with
            | -1 -> Choice2Of2(toolResult true (sprintf "malformed MCP tool name '%s'" name))
            | i ->
                let serverName = rest.Substring(0, i)
                let toolName = rest.Substring(i + 2)
                match Map.tryFind serverName servers with
                | None ->
                    Choice2Of2(toolResult true (sprintf "no connected MCP server named '%s'" serverName))
                | Some server ->
                    let arguments =
                        match plistGet "input" call with
                        | Some value -> value
                        | None -> Nil
                    Choice2Of2(callTool server toolName arguments)
        | _ -> Choice1Of2(Default "MCP dispatch requires an mcp__-prefixed :name")
