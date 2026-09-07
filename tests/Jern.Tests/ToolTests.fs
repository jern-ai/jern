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
let ``symbols finds definition sites, not mentions`` () =
    withWorkspace (fun root ->
        File.WriteAllText(
            Path.Combine(root, "src", "app.py"),
            "import greet_user\n\nclass Greeter:\n    def greet_user(self):\n        pass\n\ngreet_user()\n")
        File.WriteAllText(
            Path.Combine(root, "src", "util.fs"),
            "module Util\n\nlet greetUser name = name\ntype Greeting = { text: string }\n")
        let session = newSession root
        // Unfiltered: an outline of every definition, no mention lines.
        let outline = run session """(call-tool "symbols" (list))"""
        let content = contentOf outline
        Assert.Contains("app.py:3: class Greeter", content)
        Assert.Contains("app.py:4: def greet_user", content)
        Assert.Contains("util.fs:3: let greetUser", content)
        Assert.Contains("util.fs:4: type Greeting", content)
        Assert.DoesNotContain("app.py:1:", content)   // the import is a mention
        Assert.DoesNotContain("app.py:7:", content)   // the call is a mention
        // Filtered by name substring, case-insensitive.
        let filtered = run session """(call-tool "symbols" (list :query "greetuser"))"""
        Assert.Contains("util.fs:3: let greetUser", contentOf filtered)
        Assert.DoesNotContain("Greeting", contentOf filtered)
        // Unknown paths are tool errors, like grep.
        let missing = run session """(call-tool "symbols" (list :path "nope/"))"""
        Assert.True(isErrorOf missing))

/// The tree-sitter helper is built by native/symbols/build.sh and copied
/// beside the test binary; CI sets JERN_SYMBOLS_REQUIRED so a packaging
/// slip fails there instead of silently testing the pattern fallback.
let private symbolsRequired () =
    Environment.GetEnvironmentVariable "JERN_SYMBOLS_REQUIRED" = "1"

[<Fact>]
let ``the tree-sitter helper is present when required`` () =
    if symbolsRequired () then
        Assert.True(Symbols.available (), sprintf "jern-symbols missing at %s (queries at %s)" (Symbols.helperPath ()) (Symbols.queriesPath ()))
        Assert.Contains(".py", Symbols.languages.Value)
        Assert.Contains(".ts", Symbols.languages.Value)

[<Fact>]
let ``outline and read_symbol read a file by its definitions`` () =
    withWorkspace (fun root ->
        File.WriteAllText(
            Path.Combine(root, "src", "app.py"),
            "import os\n\n\ndef greet(name):\n    \"\"\"Say hello.\"\"\"\n    if name:\n        return f\"hi {name}\"\n    return \"hi\"\n\n\nclass Greeter:\n    def __init__(self):\n        self.count = 0\n\n    def greet(self, name):\n        self.count += 1\n        return greet(name)\n\n\nTOTAL = 3\n")
        File.WriteAllText(
            Path.Combine(root, "src", "lib.ts"),
            "export function greet(name: string): string {\n  const text = \"{\" + name; // not a brace\n  return `hi ${text}`;\n}\n\nexport class Greeter {\n  count = 0;\n  greet(name: string) {\n    this.count += 1;\n    return greet(name);\n  }\n}\n\nexport const total = () => 3;\n")
        let session = newSession root
        let exact = Symbols.available ()
        let outline = run session """(call-tool "outline" (list :path "src/app.py"))"""
        Assert.False(isErrorOf outline)
        if exact then
            // The grammar names kinds and sees the module-level constant.
            Assert.Equal("4-8: function greet — def greet(name):\n11-17: class Greeter — class Greeter:\n12-13: function __init__ — def __init__(self):\n15-17: function greet — def greet(self, name):\n20-20: constant TOTAL — TOTAL = 3", contentOf outline)
        else
            Assert.Equal("4-8: def greet — def greet(name):\n11-17: class Greeter — class Greeter:\n12-13: def __init__ — def __init__(self):\n15-17: def greet — def greet(self, name):", contentOf outline)
        let tsOutline = run session """(call-tool "outline" (list :path "src/lib.ts"))"""
        if exact then
            // Methods inside classes are definitions too, and an arrow
            // function bound by const is a function.
            Assert.Equal("1-4: function greet — export function greet(name: string): string {\n6-12: class Greeter — export class Greeter {\n8-11: method greet — greet(name: string) {\n14-14: function total — export const total = () => 3;", contentOf tsOutline)
        else
            Assert.Equal("1-4: function greet — export function greet(name: string): string {\n6-12: class Greeter — export class Greeter {\n14-14: const total — export const total = () => 3;", contentOf tsOutline)
        // One definition, by name, within a path: its lines and nothing else.
        let one = run session """(call-tool "read_symbol" (list :name "greet" :path "src/lib.ts"))"""
        if exact then
            // Both the function and the method are named greet; the method
            // is listed rather than guessed.
            Assert.False(isErrorOf one)
            Assert.StartsWith("2 definitions named 'greet'; pass path to choose one:\nsrc/lib.ts:1-4: function greet\nsrc/lib.ts:8-11: method greet", contentOf one)
        else
            Assert.False(isErrorOf one)
            Assert.StartsWith("src/lib.ts:1-4: function greet\nexport function greet(name: string): string {\n", contentOf one)
            Assert.EndsWith("return `hi ${text}`;\n}", contentOf one)
        let total = run session """(call-tool "read_symbol" (list :name "total" :path "src/lib.ts"))"""
        Assert.False(isErrorOf total)
        Assert.EndsWith("total\nexport const total = () => 3;", contentOf total)
        // Several definitions share the name: they are listed, not guessed.
        let many = run session """(call-tool "read_symbol" (list :name "greet"))"""
        Assert.False(isErrorOf many)
        Assert.StartsWith((if exact then "4 definitions named 'greet'" else "3 definitions named 'greet'") + "; pass path to choose one:\n", contentOf many)
        Assert.Contains((if exact then "src/app.py:4-8: function greet" else "src/app.py:4-8: def greet"), contentOf many)
        // Case-insensitive when nothing matches exactly; unknown names say so.
        let klass = run session """(call-tool "read_symbol" (list :name "greeter" :path "src/app.py"))"""
        Assert.StartsWith("src/app.py:11-17: class Greeter\nclass Greeter:\n", contentOf klass)
        Assert.EndsWith("        return greet(name)", contentOf klass)
        let missing = run session """(call-tool "read_symbol" (list :name "nothing"))"""
        Assert.True(isErrorOf missing)
        Assert.Contains("no definition named 'nothing'", contentOf missing)
        let plain = run session """(call-tool "outline" (list :path "README.md"))"""
        Assert.True(isErrorOf plain)
        Assert.Contains("use read_file", contentOf plain))

[<Fact>]
let ``outline falls back to patterns for a language without a grammar`` () =
    withWorkspace (fun root ->
        File.WriteAllText(
            Path.Combine(root, "src", "app.rb"),
            "class Greeter\n  def greet(name)\n    \"hi #{name}\"\n  end\nend\n")
        let session = newSession root
        let outline = run session """(call-tool "outline" (list :path "src/app.rb"))"""
        Assert.False(isErrorOf outline)
        // Indentation bounds the extent, so the closing `end` is left out.
        Assert.Equal("1-4: class Greeter — class Greeter\n2-3: def greet — def greet(name)", contentOf outline)
        let one = run session """(call-tool "read_symbol" (list :name "greet"))"""
        Assert.StartsWith("src/app.rb:2-3: def greet\n  def greet(name)\n", contentOf one))

[<Fact>]
let ``references tell definitions, calls, and mentions apart`` () =
    withWorkspace (fun root ->
        File.WriteAllText(
            Path.Combine(root, "src", "app.py"),
            "def greet(name):\n    # greet is documented here\n    return \"greet %s\" % name\n\n\nclass Greeter:\n    def greet(self, name):\n        return greet(name)\n\n\nprint(greet)\n")
        File.WriteAllText(
            Path.Combine(root, "src", "notes.rb"),
            "# greet in ruby\ndef greet; end\ngreet\n")
        let session = newSession root
        let result = run session """(call-tool "references" (list :name "greet"))"""
        Assert.False(isErrorOf result)
        let content = contentOf result
        if Symbols.available () then
            // The Python file is parsed: the comment and the string are not
            // references, the definition and the call are told apart, and
            // each sits in its enclosing definition. Ruby has no grammar,
            // so its whole-word mentions are listed as such.
            Assert.StartsWith("7 references to 'greet' in 2 files (2 definitions)\n", content)
            Assert.Contains("src/app.py:1:5: definition — def greet(name):\n", content)
            Assert.Contains("src/app.py:7:9: definition in Greeter — def greet(self, name):\n", content)
            Assert.Contains("src/app.py:8:16: call in greet — return greet(name)\n", content)
            Assert.Contains("src/app.py:11:7: identifier — print(greet)", content)
            Assert.DoesNotContain("documented", content)
            Assert.DoesNotContain("greet %s", content)
            Assert.Contains("src/notes.rb:2:5: mention — def greet; end\n", content)
            Assert.Contains("src/notes.rb:3:1: mention — greet", content)
            // Whole-word search cannot tell a comment from code.
            Assert.Contains("src/notes.rb:1:3: mention — # greet in ruby", content)
        else
            Assert.StartsWith("9 references to 'greet' in 2 files (0 definitions)\n", content)
            Assert.Contains("src/app.py:1:5: mention — def greet(name):", content)
        let none = run session """(call-tool "references" (list :name "nowhere"))"""
        Assert.Equal("(no references to 'nowhere')", contentOf none)
        let scoped = run session """(call-tool "references" (list :name "greet" :path "src/notes.rb"))"""
        Assert.StartsWith("3 references to 'greet' in 1 files (0 definitions)\n", contentOf scoped)
        let bad = run session """(call-tool "references" (list :name "greet" :path "nope"))"""
        Assert.True(isErrorOf bad))

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

/// Only runs where bubblewrap is active (Linux with working user
/// namespaces — CI); elsewhere it passes vacuously.
[<Fact>]
let ``bubblewrap confines shell writes on linux`` () =
    if Tools.linuxSandboxActive () then
        withWorkspace (fun root ->
            let session = newSession root
            let inside = run session """(call-tool "shell" (list :command "echo hi > inside.txt && cat inside.txt"))"""
            Assert.False(isErrorOf inside)
            Assert.True(File.Exists(Path.Combine(root, "inside.txt")))
            let outside = run session """(call-tool "shell" (list :command "touch /jern-sandbox-escape"))"""
            Assert.True(isErrorOf outside)
            Assert.False(File.Exists "/jern-sandbox-escape"))

/// A host that already confines jern says so; jern then runs commands
/// directly and reports the external sandbox instead of warning.
[<Fact>]
let ``an external sandbox is honoured and reported`` () =
    let previous = Environment.GetEnvironmentVariable "JERN_SANDBOX"
    try
        Environment.SetEnvironmentVariable("JERN_SANDBOX", "external")
        Assert.True(Tools.externalSandbox ())
        Assert.Equal("external", Tools.sandboxMode ())
        withWorkspace (fun root ->
            let session = newSession root
            let result = run session """(call-tool "shell" (list :command "echo confined-elsewhere"))"""
            Assert.False(isErrorOf result)
            Assert.Equal("confined-elsewhere\n", contentOf result))
        Environment.SetEnvironmentVariable("JERN_SANDBOX", "")
        Assert.False(Tools.externalSandbox ())
        Assert.NotEqual<string>("external", Tools.sandboxMode ())
    finally
        Environment.SetEnvironmentVariable("JERN_SANDBOX", previous)

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
