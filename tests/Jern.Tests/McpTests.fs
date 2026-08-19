module Jern.Tests.McpTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

/// A minimal MCP server over stdio: initialize, tools/list (one `echo`
/// tool), tools/call. Line-delimited JSON-RPC, like the real ones.
let private mockServerScript =
    String.concat "\n"
        [ "import sys, json"
          "def send(o):"
          "    sys.stdout.write(json.dumps(o) + '\\n'); sys.stdout.flush()"
          "sys.stderr.write('mock mcp server: starting\\n'); sys.stderr.flush()"
          "for line in sys.stdin:"
          "    m = json.loads(line)"
          "    method = m.get('method')"
          "    if method == 'initialize':"
          "        send({'jsonrpc':'2.0','id':m['id'],'result':{'protocolVersion':m['params']['protocolVersion'],'capabilities':{'tools':{}},'serverInfo':{'name':'mock','version':'1.0'}}})"
          "    elif method == 'notifications/initialized':"
          "        pass"
          "    elif method == 'tools/list':"
          "        send({'jsonrpc':'2.0','id':m['id'],'result':{'tools':[{'name':'echo','description':'Echo the message back.','inputSchema':{'type':'object','properties':{'message':{'type':'string'}},'required':['message']}}]}})"
          "    elif method == 'tools/call':"
          "        args = m['params'].get('arguments') or {}"
          "        send({'jsonrpc':'2.0','id':m['id'],'result':{'content':[{'type':'text','text':'echo: ' + args.get('message','')}],'isError':False}})"
          "    elif 'id' in m:"
          "        send({'jsonrpc':'2.0','id':m['id'],'error':{'code':-32601,'message':'method not found'}})" ]

let private mockSpec () : Mcp.ServerSpec =
    { name = "mock"
      command = "python3"
      args = [ "-c"; mockServerScript ]
      env = [] }

[<Fact>]
let ``mcp client connects, lists tools, and calls one`` () =
    match Mcp.connect (mockSpec ()) with
    | Error reason -> failwith reason
    | Ok server ->
        try
            match Mcp.listTools server with
            | Error reason -> failwith reason
            | Ok descriptors ->
                let descriptor = Assert.Single descriptors
                let json = Json.serialize descriptor
                Assert.Contains("\"name\":\"mcp__mock__echo\"", json)
                Assert.Contains("Echo the message back.", json)
                Assert.Contains("\"input_schema\":{\"type\":\"object\"", json)
            let result =
                Mcp.callTool server "echo" (ofList [ Keyword "message"; Obj("hi there" :> obj) ])
            Assert.Equal("""{"content":"echo: hi there","is_error":false}""", Json.serialize result)
        finally
            Mcp.shutdown server

[<Fact>]
let ``mcp dispatch reports unknown servers as tool errors`` () =
    let call =
        ofList [ Keyword "name"; Obj("mcp__ghost__boo" :> obj); Keyword "input"; Nil ]
    match Mcp.dispatch Map.empty call with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 result ->
        let json = Json.serialize result
        // ' is JSON-escaped as ', so match around it.
        Assert.Contains("no connected MCP server named", json)
        Assert.Contains("ghost", json)
        Assert.Contains("\"is_error\":true", json)

[<Fact>]
let ``jern.json mcp_servers parse into specs`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-mcpconf-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        File.WriteAllText(
            Path.Combine(root, "jern.json"),
            """{ "mcp_servers": { "gh": { "command": "npx",
                                          "args": ["-y", "server-github"],
                                          "env": { "TOKEN": "t" } } } }""")
        match Providers.load root with
        | Error message -> failwith message
        | Ok config ->
            let spec = Assert.Single config.mcpServers
            Assert.Equal("gh", spec.name)
            Assert.Equal("npx", spec.command)
            Assert.Equal<string list>([ "-y"; "server-github" ], spec.args)
            Assert.Equal<(string * string) list>([ "TOKEN", "t" ], spec.env)
    finally
        Directory.Delete(root, true)

/// The full path: an MCP tool registers in the agent env, is offered to the
/// model, survives the policy layer (:ask -> approved), executes on the
/// server, and its result reaches the next model turn.
[<Fact>]
let ``agent loop calls an MCP tool through the policy stack`` () =
    let root = Path.Combine(Path.GetTempPath(), "jern-mcploop-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    let approvals = ResizeArray<string>()
    try
        let mutable turn = 0
        let scriptedBridge: AnthropicBridge.LlmBridge =
            fun request ->
                turn <- turn + 1
                let json = Json.serialize request
                // The MCP tool is offered to the model with its schema.
                Assert.Contains("\"name\":\"mcp__mock__echo\"", json)
                match turn with
                | 1 ->
                    Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"mcp__mock__echo","input":{"message":"ping"}}]}""")
                | _ ->
                    // The loop appended the server's result.
                    Assert.Contains("echo: ping", json)
                    Choice2Of2(Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Echoed."}]}""")
        let repoAgentDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "agents", "default")
        let config =
            { Session.configIn root scriptedBridge with
                agentSources = Session.agentPackageSources repoAgentDir
                approver = Some(fun description ->
                    approvals.Add description
                    true)
                mcpServers = [ mockSpec () ] }
        let session =
            match Session.createWith config with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        match Session.runAgent session "Ping the mock server" with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 (Obj (:? string as text)) -> Assert.Equal("Echoed.", text)
        | Choice2Of2 other -> failwith ("unexpected final value: " + showVal other)
        Assert.Equal(2, turn)
        // The default policy ask-gated the MCP call.
        Assert.Contains("mcp__mock__echo", Assert.Single approvals)
    finally
        Directory.Delete(root, true)
