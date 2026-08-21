namespace Jern.Host

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open IronKernel
open IronKernel.Ast
open IronKernel.Errors

/// Host tool implementations, dispatched by name from the jern/tool-call
/// handler. Tools receive the LLM-authored input plist and return a result
/// plist `(:content "…" :is_error #t/#f)` — expected failures (missing file,
/// non-zero exit) are results the model can react to, not Kernel errors.
///
/// All paths are resolved workspace-relative and confined to the workspace
/// root. This is baseline hygiene, not the security story: policy and
/// approval handlers (M4) sit in front of every call.
module Tools =

    /// Operational limits, overridable per workspace from jern.json
    /// `"limits": {"max_file_bytes":…, "max_grep_matches":…,
    /// "max_tree_entries":…, "shell_timeout_seconds":…}` (see Providers).
    type Limits =
        { maxFileBytes: int64
          maxGrepMatches: int
          maxTreeEntries: int
          shellTimeoutSeconds: float }

    let defaultLimits =
        { maxFileBytes = 262_144L
          maxGrepMatches = 200
          maxTreeEntries = 200
          shellTimeoutSeconds = 120.0 }

    let mutable private limits = defaultLimits
    let configureLimits (value: Limits) = limits <- value

    /// F#-side plist access for tool-call payloads.
    let rec plistTryGet (key: string) (plist: LispVal) : LispVal option =
        match plist with
        | Pair { car = Keyword k; cdr = Pair rest } ->
            if k = key then Some rest.car else plistTryGet key rest.cdr
        | _ -> None

    let private stringArg key input =
        match plistTryGet key input with
        | Some (Obj (:? string as s)) -> Ok s
        | Some other -> Error(sprintf "argument '%s' must be a string, got %s" key (showVal other))
        | None -> Error(sprintf "required argument '%s' is missing" key)

    let private optionalStringArg key fallback input =
        match plistTryGet key input with
        | Some (Obj (:? string as s)) -> Ok s
        | Some (Keyword "null") | None -> Ok fallback
        | Some other -> Error(sprintf "argument '%s' must be a string, got %s" key (showVal other))

    let private result (isError: bool) (content: string) =
        ofList [ Keyword "content"; Obj(content :> obj); Keyword "is_error"; Bool isError ]

    let private ok content = result false content
    let private toolError content = result true content

    /// The symlink-free path of `path`'s deepest existing prefix, with any
    /// non-existing remainder appended verbatim. Path.GetFullPath normalizes
    /// `..` but never resolves links, so the confinement check below cannot
    /// trust it alone: a link inside the workspace can point anywhere.
    let rec private realPath (path: string) : string =
        let parent = Path.GetDirectoryName path
        if String.IsNullOrEmpty parent then path
        else
            let entryExists =
                try File.GetAttributes path |> ignore; true
                with _ -> false
            if entryExists then
                let info: FileSystemInfo =
                    if Directory.Exists path then DirectoryInfo path :> _ else FileInfo path :> _
                match info.ResolveLinkTarget true with
                | null -> Path.Combine(realPath parent, Path.GetFileName path)
                | target -> realPath target.FullName
            else
                Path.Combine(realPath parent, Path.GetFileName path)

    /// Resolve a workspace-relative path and refuse escapes from the root.
    /// Symlinks are resolved — the target and every parent, for the path and
    /// the root alike — before the containment test, so a link inside the
    /// workspace cannot smuggle reads or writes outside it.
    let private resolve (root: string) (path: string) : Result<string, string> =
        let full = Path.GetFullPath(Path.Combine(root, path))
        let real = realPath full
        let rootReal = realPath (Path.GetFullPath root)
        if real = rootReal
           || real.StartsWith(rootReal + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
            Ok full
        else
            Error(sprintf "path '%s' is outside the workspace" path)

    let private readFile root input =
        match stringArg "path" input with
        | Error e -> toolError e
        | Ok path ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if not (File.Exists full) then
                    toolError (sprintf "file '%s' does not exist" path)
                else
                    let info = FileInfo full
                    if info.Length > limits.maxFileBytes then
                        toolError (sprintf "file '%s' is %d bytes (limit %d); read a smaller file" path info.Length limits.maxFileBytes)
                    else
                        ok (File.ReadAllText full)

    let private listDir root input =
        match optionalStringArg "path" "." input with
        | Error e -> toolError e
        | Ok path ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if not (Directory.Exists full) then
                    toolError (sprintf "directory '%s' does not exist" path)
                else
                    let entries =
                        Directory.EnumerateFileSystemEntries full
                        |> Seq.map (fun entry ->
                            let name = Path.GetFileName entry
                            if Directory.Exists entry then name + "/" else name)
                        |> Seq.sort
                        |> String.concat "\n"
                    ok (if entries = "" then "(empty directory)" else entries)

    let private skippedDirs = set [ ".git"; "bin"; "obj"; "node_modules"; ".vs"; ".idea"; ".jern" ]

    /// An indented, depth-limited tree of the workspace (or a subdirectory),
    /// for cheap first-turn context and for the model to orient itself.
    let private fileTree root input =
        match optionalStringArg "path" "." input with
        | Error e -> toolError e
        | Ok path ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if not (Directory.Exists full) then
                    toolError (sprintf "directory '%s' does not exist" path)
                else
                    let lines = ResizeArray<string>()
                    let mutable truncated = false
                    let rec walk dir depth =
                        if depth <= 3 && not truncated then
                            let entries =
                                Directory.EnumerateFileSystemEntries dir
                                |> Seq.sortBy (fun e -> Path.GetFileName e)
                                |> List.ofSeq
                            for entry in entries do
                                if lines.Count >= limits.maxTreeEntries then truncated <- true
                                else
                                    let name = Path.GetFileName entry
                                    let indent = String.replicate depth "  "
                                    if Directory.Exists entry then
                                        if not (skippedDirs.Contains name) then
                                            lines.Add(indent + name + "/")
                                            walk entry (depth + 1)
                                    else
                                        lines.Add(indent + name)
                    walk full 0
                    let listing = String.concat "\n" lines
                    ok (if truncated then listing + sprintf "\n… truncated at %d entries" limits.maxTreeEntries
                        elif listing = "" then "(empty directory)"
                        else listing)

    let private grep root input =
        match stringArg "pattern" input, optionalStringArg "path" "." input with
        | Error e, _ | _, Error e -> toolError e
        | Ok pattern, Ok path ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                let regex =
                    try Ok(Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds 5.0))
                    with ex -> Error ex.Message
                match regex with
                | Error e -> toolError (sprintf "invalid pattern: %s" e)
                | Ok regex ->
                    let files =
                        if File.Exists full then Seq.singleton full
                        elif Directory.Exists full then
                            let rec walk dir = seq {
                                for entry in Directory.EnumerateFiles dir do yield entry
                                for sub in Directory.EnumerateDirectories dir do
                                    if not (skippedDirs.Contains(Path.GetFileName sub)) then
                                        yield! walk sub }
                            walk full
                        else Seq.empty
                    if Seq.isEmpty files && not (File.Exists full) && not (Directory.Exists full) then
                        toolError (sprintf "path '%s' does not exist" path)
                    else
                        let rootFull = Path.GetFullPath root
                        let matches =
                            files
                            |> Seq.collect (fun file ->
                                try
                                    File.ReadLines file
                                    |> Seq.indexed
                                    |> Seq.filter (fun (_, line) -> regex.IsMatch line)
                                    |> Seq.map (fun (i, line) ->
                                        sprintf "%s:%d: %s" (Path.GetRelativePath(rootFull, file)) (i + 1) (line.TrimEnd()))
                                with _ -> Seq.empty)
                            |> Seq.truncate (limits.maxGrepMatches + 1)
                            |> List.ofSeq
                        match matches with
                        | [] -> ok "(no matches)"
                        | m when m.Length > limits.maxGrepMatches ->
                            ok (String.concat "\n" (List.truncate limits.maxGrepMatches m)
                                + sprintf "\n… truncated at %d matches" limits.maxGrepMatches)
                        | m -> ok (String.concat "\n" m)

    let private editFile root input =
        match stringArg "path" input, stringArg "old_string" input, stringArg "new_string" input with
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> toolError e
        | Ok path, Ok oldString, Ok newString ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if not (File.Exists full) then
                    toolError (sprintf "file '%s' does not exist" path)
                elif oldString = "" then
                    toolError "old_string must not be empty"
                else
                    let text = File.ReadAllText full
                    let occurrences =
                        let rec count from acc =
                            match text.IndexOf(oldString, from, StringComparison.Ordinal) with
                            | -1 -> acc
                            | i -> count (i + oldString.Length) (acc + 1)
                        count 0 0
                    match occurrences with
                    | 0 -> toolError (sprintf "old_string not found in '%s'" path)
                    | 1 ->
                        File.WriteAllText(full, text.Replace(oldString, newString))
                        ok (sprintf "edited '%s'" path)
                    | n -> toolError (sprintf "old_string occurs %d times in '%s'; provide more context to make it unique" n path)

    /// Create (or replace) a file with the given full content. The approval
    /// prompt shows the whole content as a diff, which is easier to review
    /// than an equivalent `cat > file` shell command — and it works in agents
    /// that drop `shell` entirely.
    let private writeFile root input =
        match stringArg "path" input, stringArg "content" input with
        | Error e, _ | _, Error e -> toolError e
        | Ok path, Ok content ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if Directory.Exists full then
                    toolError (sprintf "'%s' is a directory" path)
                else
                    let existed = File.Exists full
                    let parent = Path.GetDirectoryName full
                    if not (String.IsNullOrEmpty parent) then
                        Directory.CreateDirectory parent |> ignore
                    File.WriteAllText(full, content)
                    ok (sprintf "%s '%s' (%d bytes)" (if existed then "replaced" else "wrote") path content.Length)

    /// macOS: confine shell writes to the workspace (plus temp and /dev)
    /// with sandbox-exec. Reads and network stay open — stated plainly in
    /// docs/security-model.md. Elsewhere (or if sandbox-exec is missing) the
    /// command runs unconfined and we warn once: approval is then the only gate.
    let mutable private warnedNoSandbox = false

    let private sandboxProfile (root: string) =
        let quote (p: string) = "\"" + p.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        String.concat "\n"
            [ "(version 1)"
              "(allow default)"
              "(deny file-write*)"
              sprintf "(allow file-write* (subpath %s))" (quote (Path.GetFullPath root))
              "(allow file-write* (subpath \"/private/tmp\"))"
              "(allow file-write* (subpath \"/private/var/folders\"))"
              "(allow file-write* (subpath \"/dev\"))" ]

    let private shell root input =
        match stringArg "command" input with
        | Error e -> toolError e
        | Ok command ->
            use proc = new Process()
            let sandboxExec = "/usr/bin/sandbox-exec"
            if OperatingSystem.IsMacOS() && File.Exists sandboxExec then
                proc.StartInfo.FileName <- sandboxExec
                proc.StartInfo.ArgumentList.Add "-p"
                proc.StartInfo.ArgumentList.Add(sandboxProfile root)
                proc.StartInfo.ArgumentList.Add "/bin/sh"
                proc.StartInfo.ArgumentList.Add "-c"
            else
                if not warnedNoSandbox then
                    warnedNoSandbox <- true
                    eprintfn "jern: no OS sandbox available for shell commands; approval is the only gate"
                if OperatingSystem.IsWindows() then
                    // cmd.exe /d /s /c: same contract as sh -c (one command
                    // string), /d skips AutoRun, /s keeps quote handling sane.
                    // No sandbox on Windows — docs/security-model.md says so.
                    let comspec = Environment.GetEnvironmentVariable "COMSPEC"
                    proc.StartInfo.FileName <- (if String.IsNullOrEmpty comspec then "cmd.exe" else comspec)
                    proc.StartInfo.ArgumentList.Add "/d"
                    proc.StartInfo.ArgumentList.Add "/s"
                    proc.StartInfo.ArgumentList.Add "/c"
                else
                    proc.StartInfo.FileName <- "/bin/sh"
                    proc.StartInfo.ArgumentList.Add "-c"
            proc.StartInfo.ArgumentList.Add command
            proc.StartInfo.WorkingDirectory <- root
            proc.StartInfo.RedirectStandardOutput <- true
            proc.StartInfo.RedirectStandardError <- true
            proc.StartInfo.UseShellExecute <- false
            let shellTimeout = TimeSpan.FromSeconds limits.shellTimeoutSeconds
            try
                proc.Start() |> ignore
                let stdout = proc.StandardOutput.ReadToEndAsync()
                let stderr = proc.StandardError.ReadToEndAsync()
                if proc.WaitForExit(int shellTimeout.TotalMilliseconds) then
                    let output =
                        [ stdout.Result; stderr.Result ]
                        |> List.filter (fun s -> s <> "")
                        |> String.concat "\n"
                    let output = if output = "" then "(no output)" else output
                    if proc.ExitCode = 0 then ok output
                    else toolError (sprintf "%s\n(exit code %d)" output proc.ExitCode)
                else
                    try proc.Kill(true) with _ -> ()
                    toolError (sprintf "command timed out after %.0f seconds" shellTimeout.TotalSeconds)
            with ex ->
                toolError (sprintf "failed to run command: %s" ex.Message)

    /// Dispatch an jern/tool-call payload `(:name "…" :input (…))`.
    let dispatch (root: string) (call: LispVal) : ThrowsError<LispVal> =
        match plistTryGet "name" call with
        | Some (Obj (:? string as name)) ->
            let input =
                match plistTryGet "input" call with
                | Some value -> value
                | None -> Nil
            let run =
                match name with
                | "read_file" -> Some readFile
                | "list_dir" -> Some listDir
                | "file_tree" -> Some fileTree
                | "grep" -> Some grep
                | "edit_file" -> Some editFile
                | "write_file" -> Some writeFile
                | "shell" -> Some shell
                | _ -> None
            match run with
            | Some tool ->
                try Choice2Of2(tool root input)
                with ex -> Choice2Of2(toolError (sprintf "%s failed: %s" name ex.Message))
            | None -> Choice2Of2(toolError (sprintf "unknown tool '%s'" name))
        | _ -> Choice1Of2(Default "tool-call payload must have a :name string")
