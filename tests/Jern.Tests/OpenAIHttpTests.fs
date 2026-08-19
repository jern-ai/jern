module Jern.Tests.OpenAIHttpTests

open System
open System.Net
open System.Text
open System.Threading.Tasks
open Xunit
open IronKernel.Ast
open Jern.Host

/// A one-request local server for exercising the bridge's real HTTP + SSE path.
let private serve (handler: HttpListenerContext -> unit) =
    let port =
        let l = new Sockets.TcpListener(IPAddress.Loopback, 0)
        l.Start()
        let p = (l.LocalEndpoint :?> IPEndPoint).Port
        l.Stop()
        p
    let listener = new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
    listener.Start()
    let worker =
        Task.Run(fun () ->
            while listener.IsListening do
                try handler (listener.GetContext()) with _ -> ())
    sprintf "http://127.0.0.1:%d/v1" port, listener, worker

let private request () =
    Json.deserialize """{"max_tokens":64,"messages":[{"role":"user","content":"hello"}]}"""

[<Fact>]
let ``streams sse from an openai-compatible server`` () =
    let mutable seenPath = ""
    let mutable seenBody = ""
    let url, listener, _ =
        serve (fun ctx ->
            seenPath <- ctx.Request.Url.AbsolutePath
            seenBody <- (new IO.StreamReader(ctx.Request.InputStream)).ReadToEnd()
            ctx.Response.ContentType <- "text/event-stream"
            let payload =
                String.concat "\n\n"
                    [ """data: {"model":"m","choices":[{"delta":{"content":"Hi "}}]}"""
                      """data: {"choices":[{"delta":{"content":"there"}}]}"""
                      """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}"""
                      """data: {"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""
                      "data: [DONE]"; "" ]
            let bytes = Encoding.UTF8.GetBytes payload
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
            ctx.Response.Close())
    try
        let streamed = StringBuilder()
        let bridge = OpenAIBridge.call url None "test-model" (Some(fun s -> streamed.Append s |> ignore))
        match bridge (request ()) with
        | Choice1Of2 e -> failwithf "%A" e
        | Choice2Of2 response ->
            Assert.Equal("/v1/chat/completions", seenPath)
            Assert.Contains("\"model\":\"test-model\"", seenBody)
            Assert.Contains("\"stream\":true", seenBody)
            Assert.Equal("Hi there", streamed.ToString())
            let json = Json.serialize response
            Assert.Contains("\"text\":\"Hi there\"", json)
            Assert.Contains("\"stop_reason\":\"end_turn\"", json)
            Assert.Contains("\"input_tokens\":5", json)
    finally
        listener.Stop()

[<Fact>]
let ``falls back to a plain call when the server rejects streaming`` () =
    let url, listener, _ =
        serve (fun ctx ->
            let body = (new IO.StreamReader(ctx.Request.InputStream)).ReadToEnd()
            if body.Contains "\"stream\":true" then
                ctx.Response.StatusCode <- 400
                let bytes = Encoding.UTF8.GetBytes """{"error":"stream_options unsupported"}"""
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
            else
                let bytes =
                    Encoding.UTF8.GetBytes
                        """{"model":"m","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"plain reply"}}],"usage":{"prompt_tokens":3,"completion_tokens":4}}"""
                ctx.Response.ContentType <- "application/json"
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
            ctx.Response.Close())
    try
        let streamed = StringBuilder()
        let bridge = OpenAIBridge.call url None "test-model" (Some(fun s -> streamed.Append s |> ignore))
        match bridge (request ()) with
        | Choice1Of2 e -> failwithf "%A" e
        | Choice2Of2 response ->
            // The fallback still delivers the text through the callback.
            Assert.Equal("plain reply", streamed.ToString())
            Assert.Contains("\"text\":\"plain reply\"", Json.serialize response)
    finally
        listener.Stop()

[<Fact>]
let ``an interrupt during streaming aborts without the plain-call fallback`` () =
    let mutable hits = 0
    let url, listener, _ =
        serve (fun ctx ->
            hits <- hits + 1
            ctx.Response.ContentType <- "text/event-stream"
            let payload =
                String.concat "\n\n"
                    [ """data: {"model":"m","choices":[{"delta":{"content":"chunk1"}}]}"""
                      """data: {"choices":[{"delta":{"content":"chunk2"}}]}"""
                      "data: [DONE]"; "" ]
            let bytes = Encoding.UTF8.GetBytes payload
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
            ctx.Response.Close())
    try
        let onText = fun (_: string) -> raise Interrupted
        let bridge = OpenAIBridge.call url None "test-model" (Some onText)
        match bridge (request ()) with
        | Choice2Of2 v -> failwithf "expected interrupt, got %A" v
        | Choice1Of2 error ->
            Assert.Contains("interrupted", IronKernel.Errors.showError error)
        Assert.Equal(1, hits)
    finally
        listener.Stop()
