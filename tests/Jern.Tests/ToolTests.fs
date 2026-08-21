module Jern.Tests.ToolTests

open System
open System.IO
open Xunit
open IronKernel
open IronKernel.Ast
open IronKernel.Errors
open Jern.Host

/// A throwaway workspace with a couple of files.
let private withWorkspace (body: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "jern-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    File.WriteAllText(Path.Combine(root, "README.md"), "# Sample Project\nSecond line.\n")
    File.WriteAllText(Path.Combine(root, "src", "main.txt"), "alpha\nbeta\ngamma\n")
    try body root
    finally Directory.Delete(root, true)

let private mockBridge: AnthropicBridge.LlmBridge =
    fun _ ->
        Choice2Of2 (Json.deserialize """{"role":"assistant","content":[{"type":"text","text":"unused"}],"stop_reason":"end_turn"}""")

let private newSession root =
    match Session.createIn root mockBridge with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 session -> session

let private run session source =
    match Session.runSource session "test.ikr" source with
    | Choice1Of2 error -> failwith (showError error)
    | Choice2Of2 value -> value

let private contentOf (result: LispVal) =
    match Tools.plistTryGet "content" result with
    | Some (Obj (:? string as s)) -> s
    | other -> failwithf "no :content in tool result: %A" other

let private isErrorOf (result: LispVal) =
    match Tools.plistTryGet "is_error" result with
    | Some (Bool b) -> b
    | other -> failwithf "no :is_error in tool result: %A" other

[<Fact>]
let ``read_file returns file contents through the effect path`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let result = run session """(call-tool "read_file" (list :path "README.md"))"""
        Assert.False(isErrorOf result)
        Assert.StartsWith("# Sample Project", contentOf result))

[<Fact>]
let ``list_dir lists entries with directory suffix`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let result = run session """(call-tool "list_dir" (list))"""
        let content = contentOf result
        Assert.Contains("README.md", content)
        Assert.Contains("src/", content))

[<Fact>]
let ``grep finds matches with file and line`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let result = run session """(call-tool "grep" (list :pattern "beta"))"""
        Assert.Contains("main.txt:2: beta", contentOf result))

[<Fact>]
let ``edit_file replaces a unique occurrence`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let result =
            run session
                """(call-tool "edit_file" (list :path "src/main.txt" :old_string "beta" :new_string "delta"))"""
        Assert.False(isErrorOf result)
        Assert.Contains("delta", File.ReadAllText(Path.Combine(root, "src", "main.txt"))))

[<Fact>]
let ``edit_file refuses an ambiguous replacement`` () =
    withWorkspace (fun root ->
        File.WriteAllText(Path.Combine(root, "dup.txt"), "x\nx\n")
        let session = newSession root
        let result =
            run session
                """(call-tool "edit_file" (list :path "dup.txt" :old_string "x" :new_string "y"))"""
        Assert.True(isErrorOf result)
        Assert.Contains("2 times", contentOf result))

[<Fact>]
let ``shell runs in the workspace and reports exit status`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let okResult = run session """(call-tool "shell" (list :command "echo hi"))"""
        Assert.False(isErrorOf okResult)
        Assert.Equal("hi\n", contentOf okResult)
        let failed = run session """(call-tool "shell" (list :command "exit 3"))"""
        Assert.True(isErrorOf failed)
        Assert.Contains("exit code 3", contentOf failed))

[<Fact>]
let ``paths outside the workspace are refused`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let result = run session """(call-tool "read_file" (list :path "../../etc/passwd"))"""
        Assert.True(isErrorOf result)
        Assert.Contains("outside the workspace", contentOf result))

/// Path.GetFullPath normalizes `..` but not links: a symlink inside the
/// workspace pointing outside must not smuggle reads or writes past the
/// containment check — through the link itself or through a linked parent.
[<Fact>]
let ``symlinks cannot escape the workspace`` () =
    if not (OperatingSystem.IsWindows()) then
        withWorkspace (fun root ->
            let outside = Path.Combine(Path.GetTempPath(), "jern-outside-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory outside |> ignore
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret\n")
            try
                Directory.CreateSymbolicLink(Path.Combine(root, "esc"), outside) |> ignore
                File.CreateSymbolicLink(Path.Combine(root, "esc.txt"), Path.Combine(outside, "secret.txt")) |> ignore
                let session = newSession root
                // Through a linked directory…
                let viaDir = run session """(call-tool "read_file" (list :path "esc/secret.txt"))"""
                Assert.True(isErrorOf viaDir)
                Assert.Contains("outside the workspace", contentOf viaDir)
                // …through a linked file…
                let viaFile = run session """(call-tool "read_file" (list :path "esc.txt"))"""
                Assert.True(isErrorOf viaFile)
                // …and for writes.
                let write =
                    run session
                        """(call-tool "edit_file" (list :path "esc/secret.txt" :old_string "secret" :new_string "gone"))"""
                Assert.True(isErrorOf write)
                Assert.Equal("secret\n", File.ReadAllText(Path.Combine(outside, "secret.txt")))
                // A link to a place inside the workspace still works.
                File.CreateSymbolicLink(Path.Combine(root, "inside.txt"), Path.Combine(root, "README.md")) |> ignore
                let inside = run session """(call-tool "read_file" (list :path "inside.txt"))"""
                Assert.False(isErrorOf inside)
            finally
                Directory.Delete(outside, true))

[<Fact>]
let ``write_file creates files and parent directories`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let result =
            run session """(call-tool "write_file" (list :path "docs/new/note.md" :content "hello\n"))"""
        Assert.False(isErrorOf result)
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(root, "docs", "new", "note.md")))
        // Replacing an existing file says so.
        let replaced =
            run session """(call-tool "write_file" (list :path "docs/new/note.md" :content "bye\n"))"""
        Assert.False(isErrorOf replaced)
        Assert.Contains("replaced", contentOf replaced)
        Assert.Equal("bye\n", File.ReadAllText(Path.Combine(root, "docs", "new", "note.md")))
        // Outside the workspace: refused.
        let escape = run session """(call-tool "write_file" (list :path "../evil.txt" :content "x"))"""
        Assert.True(isErrorOf escape)
        Assert.Contains("outside the workspace", contentOf escape))

[<Fact>]
let ``unknown tools come back as tool errors`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let result = run session """(call-tool "rm_rf" (list))"""
        Assert.True(isErrorOf result)
        Assert.Contains("unknown tool", contentOf result))

[<Fact>]
let ``tools-for-llm serializes to messages api tool definitions`` () =
    withWorkspace (fun root ->
        let session = newSession root
        let tools = run session "(tools-for-llm)"
        let json = Json.serialize tools
        Assert.Contains("\"name\":\"read_file\"", json)
        Assert.Contains("\"input_schema\":{\"type\":\"object\"", json)
        Assert.Contains("\"required\":[\"path\",\"old_string\",\"new_string\"]", json))

/// The M2 exit criterion, network-free: a scripted LLM asks for read_file via
/// function-calling; the round trip dispatches it and answers from the result.
[<Fact>]
let ``scripted llm round-trip calls read_file and answers from its result`` () =
    withWorkspace (fun root ->
        let mutable turn = 0
        let scriptedBridge: AnthropicBridge.LlmBridge =
            fun request ->
                turn <- turn + 1
                if turn = 1 then
                    // Ask for the tool.
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"toolu_1","name":"read_file","input":{"path":"README.md"}}]}""")
                else
                    // Answer with the first line of the tool_result we received.
                    let json = Json.serialize request
                    Assert.Contains("\"type\":\"tool_result\"", json)
                    Assert.Contains("# Sample Project", json)
                    Choice2Of2 (Json.deserialize """{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"The first heading is '# Sample Project'."}]}""")
        let session =
            match Session.createIn root scriptedBridge with
            | Choice1Of2 error -> failwith (showError error)
            | Choice2Of2 s -> s
        let script = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "tool-roundtrip.ikr"))
        match Session.runSource session "tool-roundtrip.ikr" script with
        | Choice1Of2 error -> failwith (showError error)
        | Choice2Of2 _ -> Assert.Equal(2, turn))
