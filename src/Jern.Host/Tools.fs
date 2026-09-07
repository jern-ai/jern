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
          shellTimeoutSeconds: float
          /// Wall-clock cap on one run_tests call; suites run longer than
          /// commands.
          testTimeoutSeconds: float
          /// Wall-clock cap on one kernel_eval program (model-authored
          /// Kernel code has no other stop for a pure loop).
          evalTimeoutSeconds: float }

    let defaultLimits =
        { maxFileBytes = 262_144L
          maxGrepMatches = 200
          maxTreeEntries = 200
          shellTimeoutSeconds = 120.0
          testTimeoutSeconds = 600.0
          evalTimeoutSeconds = 30.0 }

    let mutable private limits = defaultLimits
    let configureLimits (value: Limits) = limits <- value
    let currentLimits () = limits

    /// The workspace's test command (jern.json "test_command"), the only
    /// command run_tests ever runs. None until configured.
    let mutable private testCommand: string option = None
    let configureTestCommand (value: string option) = testCommand <- value

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
    /// The entries of a tree built from workspace-relative file paths,
    /// depth-limited and sorted like the directory walk: directories first
    /// come where their names sort, each with a trailing slash.
    let private treeFromPaths (relativeRoot: string) (paths: string list) =
        let prefix = if relativeRoot = "" || relativeRoot = "." then "" else relativeRoot.TrimEnd('/') + "/"
        let under =
            paths
            |> List.choose (fun p -> if prefix = "" then Some p elif p.StartsWith prefix then Some(p.Substring prefix.Length) else None)
            |> List.filter (fun p -> p <> "" && not (p.Split('/') |> Array.exists skippedDirs.Contains))
        let lines = ResizeArray<string>()
        let mutable truncated = false
        let rec emit (depth: int) (entries: string list) =
            // Group by first segment; a file is a segment with nothing after.
            let groups =
                entries
                |> List.groupBy (fun p -> match p.IndexOf '/' with -1 -> p, false | i -> p.Substring(0, i), true)
                |> List.sortBy (fun ((name, _), _) -> name)
            for (name, isDir), members in groups do
                if lines.Count >= limits.maxTreeEntries then truncated <- true
                elif not truncated then
                    let indent = String.replicate depth "  "
                    if isDir then
                        lines.Add(indent + name + "/")
                        if depth + 1 <= 3 then
                            emit (depth + 1) (members |> List.map (fun p -> p.Substring(name.Length + 1)))
                    else lines.Add(indent + name)
        emit 0 under
        lines, truncated

    /// An indented, depth-limited tree of the workspace (or a subdirectory),
    /// for cheap first-turn context and for the model to orient itself.
    /// Inside a git repository the tree is what git tracks or would track:
    /// ignored files (build output, dependencies) are left out.
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
                    let relative = Path.GetRelativePath(Path.GetFullPath root, full).Replace('\\', '/')
                    let fromGit =
                        if Git.isRepo root then
                            match Git.listFiles root (if relative = "." then "." else relative) with
                            | Ok paths -> Some(treeFromPaths relative paths)
                            | Error _ -> None
                        else None
                    let lines, truncated =
                        match fromGit with
                        | Some result -> result
                        | None ->
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
                            lines, truncated
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

    /// Definition-site patterns per file extension, with the symbol name in
    /// group "n". Deliberately heuristic — line-anchored regexes, not a real
    /// parser — but they match *definitions* rather than mentions, which is
    /// what grep cannot distinguish. Noise is bounded by the match cap.
    let private symbolPatterns =
        let p kind pattern = Regex(pattern, RegexOptions.Compiled), (kind: string)
        [ [".fs"; ".fsx"],
          [ p "let" @"^\s{0,4}let\s+(?:rec\s+)?(?:inline\s+)?(?:private\s+|internal\s+)?(?<n>[A-Za-z_][\w']*)"
            p "type" @"^\s*type\s+(?:private\s+|internal\s+)?(?<n>[A-Za-z_][\w']*)"
            p "module" @"^\s*module\s+(?:rec\s+)?(?:private\s+|internal\s+)?(?<n>[A-Za-z_][\w'.]*)"
            p "member" @"^\s*(?:static\s+)?member\s+(?:private\s+)?(?:_|this|[a-z]\w*)\.(?<n>[A-Za-z_][\w']*)" ]
          [".cs"],
          [ p "type" @"^\s*(?:public\s+|internal\s+|private\s+|protected\s+|static\s+|sealed\s+|abstract\s+|partial\s+)*(?:class|interface|struct|enum|record)\s+(?<n>[A-Za-z_]\w*)"
            p "method" @"^\s*(?:public\s+|internal\s+|private\s+|protected\s+|static\s+|async\s+|virtual\s+|override\s+|sealed\s+)+[\w<>\[\],\.\?]+\s+(?<n>[A-Za-z_]\w*)\s*\(" ]
          [".py"],
          [ p "def" @"^\s*(?:async\s+)?def\s+(?<n>[A-Za-z_]\w*)"
            p "class" @"^\s*class\s+(?<n>[A-Za-z_]\w*)" ]
          [".js"; ".jsx"; ".ts"; ".tsx"; ".mjs"],
          [ p "function" @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(?<n>[A-Za-z_$][\w$]*)"
            p "class" @"^\s*(?:export\s+)?(?:default\s+)?class\s+(?<n>[A-Za-z_$][\w$]*)"
            p "const" @"^\s*(?:export\s+)?(?:const|let|var)\s+(?<n>[A-Za-z_$][\w$]*)\s*=\s*(?:async\s+)?(?:\(|function|[\w$,\s()]*=>)"
            p "type" @"^\s*(?:export\s+)?(?:type|interface|enum)\s+(?<n>[A-Za-z_$][\w$]*)" ]
          [".go"],
          [ p "func" @"^func\s+(?:\([^)]*\)\s+)?(?<n>[A-Za-z_]\w*)"
            p "type" @"^type\s+(?<n>[A-Za-z_]\w*)" ]
          [".rs"],
          [ p "fn" @"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?(?:unsafe\s+)?fn\s+(?<n>[A-Za-z_]\w*)"
            p "type" @"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:struct|enum|trait|union)\s+(?<n>[A-Za-z_]\w*)"
            p "impl" @"^\s*impl(?:<[^>]*>)?\s+(?<n>[A-Za-z_]\w*)" ]
          [".rb"],
          [ p "def" @"^\s*def\s+(?:self\.)?(?<n>[A-Za-z_]\w*[?!]?)"
            p "class" @"^\s*(?:class|module)\s+(?<n>[A-Z]\w*)" ]
          [".java"; ".kt"; ".kts"; ".scala"],
          [ p "type" @"^\s*(?:public\s+|private\s+|protected\s+|internal\s+|abstract\s+|final\s+|open\s+|sealed\s+|data\s+|case\s+|static\s+)*(?:class|interface|enum|object|trait|record)\s+(?<n>[A-Za-z_]\w*)"
            p "fun" @"^\s*(?:public\s+|private\s+|protected\s+|internal\s+|override\s+|suspend\s+|static\s+|final\s+)*(?:fun|def)\s+(?<n>[A-Za-z_]\w*)" ]
          [".ikr"; ".lisp"; ".scm"; ".clj"; ".cljs"; ".el"],
          [ p "define" @"^\s*\((?:define|defun|defn|defmacro|defvar)[-a-z!?]*\s+\(?(?<n>[^\s()""]+)"
            p "define" @"^\s*\((?:define-tool|deftest)\s+""(?<n>[^""]+)""" ]
          [".sh"; ".bash"; ".zsh"],
          [ p "function" @"^\s*(?:function\s+)?(?<n>[A-Za-z_]\w*)\s*\(\)\s*\{" ] ]
        |> List.collect (fun (extensions, patterns) ->
            extensions |> List.map (fun ext -> ext, patterns))
        |> dict

    /// Semantic-ish code search: definition sites only, across the workspace
    /// (or one file/directory), optionally filtered by a name substring. The
    /// model orients itself with this instead of grepping every mention.
    let private symbols root input =
        match optionalStringArg "query" "" input, optionalStringArg "path" "." input with
        | Error e, _ | _, Error e -> toolError e
        | Ok query, Ok path ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
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
                if not (File.Exists full) && not (Directory.Exists full) then
                    toolError (sprintf "path '%s' does not exist" path)
                else
                    let rootFull = Path.GetFullPath root
                    let matches =
                        files
                        |> Seq.collect (fun file ->
                            match symbolPatterns.TryGetValue(Path.GetExtension(file).ToLowerInvariant()) with
                            | false, _ -> Seq.empty
                            | true, patterns ->
                                try
                                    File.ReadLines file
                                    |> Seq.indexed
                                    |> Seq.choose (fun (i, line) ->
                                        patterns
                                        |> List.tryPick (fun (regex, kind) ->
                                            let m = regex.Match line
                                            if m.Success then Some(m.Groups.["n"].Value, kind) else None)
                                        |> Option.bind (fun (name, kind) ->
                                            if query = "" || name.Contains(query, StringComparison.OrdinalIgnoreCase) then
                                                Some(sprintf "%s:%d: %s %s"
                                                         (Path.GetRelativePath(rootFull, file)) (i + 1) kind name)
                                            else None))
                                with _ -> Seq.empty)
                        |> Seq.truncate (limits.maxGrepMatches + 1)
                        |> List.ofSeq
                    match matches with
                    | [] -> ok "(no symbols found)"
                    | m when m.Length > limits.maxGrepMatches ->
                        ok (String.concat "\n" (List.truncate limits.maxGrepMatches m)
                            + sprintf "\n… truncated at %d matches; narrow with query or path" limits.maxGrepMatches)
                    | m -> ok (String.concat "\n" m)

    // -----------------------------------------------------------------------
    // Reading by symbol. The definition patterns above find where things
    // start; the extent of a definition follows the language's own shape:
    // braces for C-like languages, parentheses for Lisps, indentation for the
    // rest. Approximate by design, bounded, and deterministic, so the model
    // can read one definition instead of the file around it.

    /// Every definition site in a file: line index, kind, name, and the line.
    let private definitionsOf (file: string) (lines: string[]) =
        match symbolPatterns.TryGetValue(Path.GetExtension(file).ToLowerInvariant()) with
        | false, _ -> []
        | true, patterns ->
            lines
            |> Array.toList
            |> List.indexed
            |> List.choose (fun (i, line) ->
                patterns
                |> List.tryPick (fun (regex, kind) ->
                    let m = regex.Match line
                    if m.Success then Some(i, kind, m.Groups.["n"].Value, line.TrimEnd()) else None))

    let private indentOf (line: string) = line.Length - line.TrimStart().Length

    let private braceLanguages =
        set [ ".cs"; ".java"; ".kt"; ".kts"; ".scala"; ".go"; ".rs"; ".js"; ".jsx"; ".ts"; ".tsx"; ".mjs"; ".sh"; ".bash"; ".zsh" ]
    let private parenLanguages = set [ ".ikr"; ".lisp"; ".scm"; ".clj"; ".cljs"; ".el" ]

    /// Walk a line counting a bracket pair outside string literals and after
    /// no line comment; returns the depth change and whether a bracket opened.
    let private bracketDelta (openChar: char) (closeChar: char) (lineComment: string) (line: string) =
        let mutable depth = 0
        let mutable opened = false
        let mutable quote: char option = None
        let mutable j = 0
        let mutable stop = false
        while not stop && j < line.Length do
            let c = line.[j]
            match quote with
            | Some q ->
                if c = '\\' then j <- j + 1
                elif c = q then quote <- None
            | None ->
                if c = '"' || c = '\'' || c = '`' then quote <- Some c
                elif lineComment <> "" && line.AsSpan(j).StartsWith(lineComment.AsSpan()) then stop <- true
                elif c = openChar then
                    depth <- depth + 1
                    opened <- true
                elif c = closeChar then depth <- depth - 1
            j <- j + 1
        depth, opened

    /// The last line index of the definition that starts at `start`.
    let private extentEnd (ext: string) (lines: string[]) (start: int) =
        let last = lines.Length - 1
        let bracketed openChar closeChar lineComment =
            let mutable depth = 0
            let mutable opened = false
            let mutable finish = start
            let mutable i = start
            let mutable stop = false
            while not stop && i <= last do
                let delta, openedHere = bracketDelta openChar closeChar lineComment lines.[i]
                depth <- depth + delta
                opened <- opened || openedHere
                if opened && depth <= 0 then
                    finish <- i
                    stop <- true
                elif not opened && i - start >= 2 then
                    // No block within three lines: a one-line declaration.
                    finish <- start
                    stop <- true
                else
                    finish <- i
                    i <- i + 1
            finish
        if braceLanguages.Contains ext then
            bracketed '{' '}' (if ext = ".sh" || ext = ".bash" || ext = ".zsh" then "#" else "//")
        elif parenLanguages.Contains ext then bracketed '(' ')' ";"
        else
            let baseIndent = indentOf lines.[start]
            let mutable finish = start
            let mutable i = start + 1
            let mutable stop = false
            while not stop && i <= last do
                let line = lines.[i]
                if line.Trim() = "" then i <- i + 1
                elif indentOf line > baseIndent then
                    finish <- i
                    i <- i + 1
                else stop <- true
            finish

    let private signatureOf (line: string) =
        let t = line.Trim()
        if t.Length > 160 then t.Substring(0, 160) + "…" else t

    /// Every definition in a file with its extent, 0-based and inclusive:
    /// exact from jern-symbols when its grammar covers the language, else
    /// the patterns above with the bracket/indentation extent.
    let private definitionsWithExtent (file: string) (lines: string[]) =
        match Symbols.outline file with
        | Some defs ->
            defs |> List.map (fun d -> d.start - 1, max (d.start - 1) (d.finish - 1), d.kind, d.name, d.signature)
        | None ->
            let ext = Path.GetExtension(file).ToLowerInvariant()
            definitionsOf file lines
            |> List.map (fun (i, kind, name, line) -> i, extentEnd ext lines i, kind, name, signatureOf line)

    let private hasDefinitions (file: string) =
        symbolPatterns.ContainsKey(Path.GetExtension(file).ToLowerInvariant()) || Symbols.supports file

    let private walkFiles (full: string) =
        if File.Exists full then Seq.singleton full
        elif Directory.Exists full then
            let rec walk dir = seq {
                for entry in Directory.EnumerateFiles dir do yield entry
                for sub in Directory.EnumerateDirectories dir do
                    if not (skippedDirs.Contains(Path.GetFileName sub)) then
                        yield! walk sub }
            walk full
        else Seq.empty

    /// Every definition in one file with its kind, extent, and signature line.
    let private outline root input =
        match stringArg "path" input with
        | Error e -> toolError e
        | Ok path ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if not (File.Exists full) then toolError (sprintf "file '%s' does not exist" path)
                elif not (hasDefinitions full) then
                    toolError (sprintf "no definition patterns for '%s' files; use read_file" (Path.GetExtension(full).ToLowerInvariant()))
                else
                    let lines = File.ReadAllLines full
                    match definitionsWithExtent full lines with
                    | [] -> ok "(no definitions found)"
                    | defs ->
                        let shown =
                            defs
                            |> List.truncate limits.maxGrepMatches
                            |> List.map (fun (start, finish, kind, name, signature) ->
                                sprintf "%d-%d: %s %s — %s" (start + 1) (finish + 1) kind name signature)
                        let more =
                            if defs.Length > limits.maxGrepMatches then sprintf "\n… truncated at %d definitions" limits.maxGrepMatches else ""
                        ok (String.concat "\n" shown + more)

    let private maxSymbolLines = 400

    /// Definitions named `name` across `files`: (file, start, finish, kind,
    /// name), 0-based inclusive lines. Files the helper parses are answered
    /// exactly; the rest by pattern. `exact` false compares ignoring case.
    let private definitionsNamed (files: string list) (name: string) (exact: bool) =
        let supported, others = files |> List.partition Symbols.supports
        let fromHelper, others =
            match Symbols.definitions supported (Some name) (not exact) with
            | Some defs ->
                defs |> List.map (fun d -> d.file, d.start - 1, max (d.start - 1) (d.finish - 1), d.kind, d.name), others
            | None -> [], files
        let comparison = if exact then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase
        let fromPatterns =
            others
            |> Seq.collect (fun file ->
                if not (symbolPatterns.ContainsKey(Path.GetExtension(file).ToLowerInvariant())) then Seq.empty
                else
                    try
                        let lines = File.ReadAllLines file
                        let ext = Path.GetExtension(file).ToLowerInvariant()
                        definitionsOf file lines
                        |> List.filter (fun (_, _, n, _) -> String.Equals(n, name, comparison))
                        |> List.map (fun (i, kind, n, _) -> file, i, extentEnd ext lines i, kind, n)
                        |> Seq.ofList
                    with _ -> Seq.empty)
            |> List.ofSeq
        fromHelper @ fromPatterns
        |> List.sortBy (fun (file, start, _, _, _) -> file, start)
        |> List.truncate 50

    /// The source of one definition by name, across the workspace or under a
    /// path; several matches are listed so the model can name the file.
    let private readSymbol root input =
        match stringArg "name" input, optionalStringArg "path" "." input with
        | Error e, _ | _, Error e -> toolError e
        | Ok name, Ok path ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                let rootFull = Path.GetFullPath root
                if not (File.Exists full) && not (Directory.Exists full) then
                    toolError (sprintf "path '%s' does not exist" path)
                else
                    let files = walkFiles full |> List.ofSeq
                    let candidates =
                        match definitionsNamed files name true with
                        | [] -> definitionsNamed files name false
                        | exact -> exact
                    match candidates with
                    | [] -> toolError (sprintf "no definition named '%s' found; symbols with a query finds partial names" name)
                    | [ (file, start, finish, kind, defined) ] ->
                        let lines = File.ReadAllLines file
                        let finish = min finish (lines.Length - 1)
                        let shownEnd = min finish (start + maxSymbolLines - 1)
                        let body = String.concat "\n" lines.[start .. shownEnd]
                        let more = if shownEnd < finish then sprintf "\n… %d more lines; read_file for the rest" (finish - shownEnd) else ""
                        ok (sprintf "%s:%d-%d: %s %s\n%s%s" (Path.GetRelativePath(rootFull, file)) (start + 1) (finish + 1) kind defined body more)
                    | many ->
                        let listed =
                            many
                            |> List.map (fun (file, start, finish, kind, defined) ->
                                sprintf "%s:%d-%d: %s %s" (Path.GetRelativePath(rootFull, file)) (start + 1) (finish + 1) kind defined)
                        ok (sprintf "%d definitions named '%s'; pass path to choose one:\n%s" many.Length name (String.concat "\n" listed))

    /// Every mention of a name across the workspace or under a path, outside
    /// strings and comments where a grammar is available, each labelled with
    /// what it is (definition, call, type, …) and the definition it sits in.
    /// Files without a grammar are searched for the whole word.
    let private references root input =
        match stringArg "name" input, optionalStringArg "path" "." input with
        | Error e, _ | _, Error e -> toolError e
        | Ok name, Ok path ->
            if name.Trim() = "" then toolError "name must not be empty"
            else
                match resolve root path with
                | Error e -> toolError e
                | Ok full ->
                    let rootFull = Path.GetFullPath root
                    if not (File.Exists full) && not (Directory.Exists full) then
                        toolError (sprintf "path '%s' does not exist" path)
                    else
                        let files = walkFiles full |> List.ofSeq
                        let supported, others = files |> List.partition Symbols.supports
                        let exact, others =
                            match Symbols.references supported name with
                            | Some refs -> refs |> List.map (fun r -> r.file, r.line, r.column, r.kind, r.scope, r.text), others
                            | None -> [], files
                        let word = Regex(@"(?<![\w$])" + Regex.Escape name + @"(?![\w$])", RegexOptions.Compiled)
                        let textual =
                            others
                            |> Seq.filter (fun file -> symbolPatterns.ContainsKey(Path.GetExtension(file).ToLowerInvariant()))
                            |> Seq.collect (fun file ->
                                try
                                    File.ReadLines file
                                    |> Seq.indexed
                                    |> Seq.collect (fun (i, line) ->
                                        word.Matches line
                                        |> Seq.map (fun m -> file, i + 1, m.Index + 1, "mention", "", line.Trim()))
                                    |> List.ofSeq
                                    |> Seq.ofList
                                with _ -> Seq.empty)
                            |> List.ofSeq
                        let all =
                            exact @ textual
                            |> List.sortBy (fun (file, line, column, _, _, _) -> file, line, column)
                        match all with
                        | [] -> ok (sprintf "(no references to '%s')" name)
                        | _ ->
                            let shown =
                                all
                                |> List.truncate limits.maxGrepMatches
                                |> List.map (fun (file, line, column, kind, scope, text) ->
                                    let where = if scope = "" then kind else sprintf "%s in %s" kind scope
                                    sprintf "%s:%d:%d: %s — %s" (Path.GetRelativePath(rootFull, file)) line column where text)
                            let fileCount = all |> List.map (fun (f, _, _, _, _, _) -> f) |> List.distinct |> List.length
                            let definitions = all |> List.filter (fun (_, _, _, k, _, _) -> k = "definition") |> List.length
                            let header =
                                sprintf "%d references to '%s' in %d files (%d definitions)" all.Length name fileCount definitions
                            let more =
                                if all.Length > limits.maxGrepMatches then sprintf "\n… truncated at %d; narrow with path" limits.maxGrepMatches else ""
                            ok (header + "\n" + String.concat "\n" shown + more)

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

    // -----------------------------------------------------------------------
    // Edits that fail closed. edit_symbol replaces exactly one definition's
    // lines, found the way read_symbol finds them; apply_patch applies a
    // unified diff whose every hunk must match the file (at its stated line
    // first, then anywhere it matches exactly once). Neither edits a file
    // that does not look the way the model was told it looks.

    let private splitKeepingEnding (text: string) =
        let crlf = text.Contains "\r\n"
        let body = if crlf then text.Replace("\r\n", "\n") else text
        let trailing = body.EndsWith "\n"
        let lines = body.Split('\n')
        let lines = if trailing then lines.[.. lines.Length - 2] else lines
        lines, crlf, trailing

    let private joinWithEnding (lines: string[]) (crlf: bool) (trailing: bool) =
        let body = String.Join("\n", lines) + (if trailing && lines.Length > 0 then "\n" else "")
        if crlf then body.Replace("\n", "\r\n") else body

    /// The one definition named `name` in `file`, as 0-based inclusive lines.
    let private symbolIn (root: string) (file: string) (name: string) =
        match definitionsNamed [ file ] name true with
        | [] ->
            match definitionsNamed [ file ] name false with
            | [] -> Error(sprintf "no definition named '%s' in '%s'" name (Path.GetRelativePath(Path.GetFullPath root, file)))
            | [ (_, start, finish, kind, defined) ] -> Ok(start, finish, kind, defined)
            | many -> Error(sprintf "%d definitions match '%s' in this file (%s); name one exactly" many.Length name (many |> List.map (fun (_, st, _, k, n) -> sprintf "%s %s at line %d" k n (st + 1)) |> String.concat ", "))
        | [ (_, start, finish, kind, defined) ] -> Ok(start, finish, kind, defined)
        | many -> Error(sprintf "'%s' is defined %d times in this file (lines %s); edit_file the one you mean" name many.Length (many |> List.map (fun (_, st, _, _, _) -> string (st + 1)) |> String.concat ", "))

    /// Lines a symbol edit would change: the old extent plus the new source,
    /// counted like edit_file counts old_string plus new_string. None when
    /// the symbol cannot be found (the tool will refuse; nothing changes).
    let projectedSymbolEdit (root: string) (path: string) (name: string) (newSource: string) : int64 option =
        try
            match resolve root path with
            | Ok full when File.Exists full ->
                match symbolIn root full name with
                | Ok (start, finish, _, _) ->
                    let added = (splitKeepingEnding newSource |> fun (l, _, _) -> l.Length)
                    Some(int64 (finish - start + 1) + int64 added)
                | Error _ -> None
            | _ -> None
        with _ -> None

    let private editSymbol root input =
        match stringArg "path" input, stringArg "name" input, stringArg "new_source" input with
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> toolError e
        | Ok path, Ok name, Ok newSource ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if not (File.Exists full) then toolError (sprintf "file '%s' does not exist" path)
                elif not (hasDefinitions full) then toolError (sprintf "no definition patterns for '%s' files; use edit_file" (Path.GetExtension(full).ToLowerInvariant()))
                elif newSource.Trim() = "" then toolError "new_source must not be empty; to remove a definition use edit_file"
                else
                    match symbolIn root full name with
                    | Error e -> toolError e
                    | Ok (start, finish, kind, defined) ->
                        let lines, crlf, trailing = splitKeepingEnding (File.ReadAllText full)
                        let finish = min finish (lines.Length - 1)
                        let replacement, _, _ = splitKeepingEnding newSource
                        let updated = Array.concat [ lines.[.. start - 1]; replacement; lines.[finish + 1 ..] ]
                        File.WriteAllText(full, joinWithEnding updated crlf trailing)
                        ok (sprintf "replaced %s %s in '%s' (lines %d-%d, now %d-%d)" kind defined path (start + 1) (finish + 1) (start + 1) (start + replacement.Length))

    type private Hunk = { oldStart: int; before: string list; after: string list; text: string }

    let private hunkHeader = Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled)

    /// The hunks of a unified diff for one file; headers (---/+++/diff/index)
    /// are skipped, a `\ No newline at end of file` marker ignored.
    let private parseHunks (patch: string) : Result<Hunk list, string> =
        // The patch's own trailing newline is not a blank context line.
        let lines = patch.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
        let hunks = ResizeArray<Hunk>()
        let mutable current: (int * ResizeArray<string> * ResizeArray<string> * ResizeArray<string>) option = None
        let mutable problem: string option = None
        let flush () =
            match current with
            | Some (start, before, after, text) ->
                hunks.Add { oldStart = start; before = List.ofSeq before; after = List.ofSeq after; text = String.Join("\n", text) }
                current <- None
            | None -> ()
        for raw in lines do
            if problem.IsNone then
                let m = hunkHeader.Match raw
                if m.Success then
                    flush ()
                    current <- Some(int m.Groups.[1].Value, ResizeArray(), ResizeArray(), ResizeArray [ raw ])
                else
                    match current with
                    | None ->
                        if raw.StartsWith "--- " || raw.StartsWith "+++ " || raw.StartsWith "diff " || raw.StartsWith "index " || raw.Trim() = "" then ()
                        else problem <- Some(sprintf "unexpected line before the first hunk: %s" raw)
                    | Some (_, before, after, text) ->
                        if raw.StartsWith "\\" then ()
                        elif raw = "" then
                            // A blank context line whose leading space was lost.
                            before.Add ""; after.Add ""; text.Add raw
                        else
                            match raw.[0] with
                            | ' ' -> before.Add(raw.Substring 1); after.Add(raw.Substring 1); text.Add raw
                            | '-' -> before.Add(raw.Substring 1); text.Add raw
                            | '+' -> after.Add(raw.Substring 1); text.Add raw
                            | _ -> problem <- Some(sprintf "a hunk line must start with ' ', '-', or '+': %s" raw)
        flush ()
        match problem with
        | Some p -> Error p
        | None when hunks.Count = 0 -> Error "the patch has no @@ hunks"
        | None -> Ok(List.ofSeq hunks)

    /// Lines a patch would change: its removed plus added lines.
    let patchDelta (patch: string) : int64 =
        match parseHunks patch with
        | Ok hunks ->
            hunks
            |> List.sumBy (fun h ->
                h.text.Split('\n') |> Array.filter (fun l -> l.Length > 0 && (l.[0] = '-' || l.[0] = '+') && not (l.StartsWith "---") && not (l.StartsWith "+++")) |> Array.length |> int64)
        | Error _ -> 0L

    /// Where a hunk's pre-image sits in `lines`: at the stated line when it
    /// matches there, else the single place it matches; None otherwise.
    let private locate (lines: string[]) (hunk: Hunk) (offset: int) =
        let before = Array.ofList hunk.before
        let matchesAt i =
            i >= 0 && i + before.Length <= lines.Length
            && Seq.forall2 (=) (Seq.ofArray lines.[i .. i + before.Length - 1]) (Seq.ofArray before)
        let stated = hunk.oldStart - 1 + offset
        if before.Length = 0 then Some(max 0 (min stated lines.Length))
        elif matchesAt stated then Some stated
        else
            let candidates = [ for i in 0 .. lines.Length - before.Length do if matchesAt i then yield i ]
            match candidates with
            | [ one ] -> Some one
            | _ -> None

    let private applyPatch root input =
        match stringArg "path" input, stringArg "patch" input with
        | Error e, _ | _, Error e -> toolError e
        | Ok path, Ok patch ->
            match resolve root path with
            | Error e -> toolError e
            | Ok full ->
                if not (File.Exists full) then toolError (sprintf "file '%s' does not exist; write_file creates files" path)
                else
                    match parseHunks patch with
                    | Error e -> toolError e
                    | Ok hunks ->
                        let original, crlf, trailing = splitKeepingEnding (File.ReadAllText full)
                        let mutable lines = original
                        let mutable offset = 0
                        let mutable failure: string option = None
                        for hunk in hunks do
                            if failure.IsNone then
                                match locate lines hunk offset with
                                | None ->
                                    let first = hunk.before |> List.tryHead |> Option.defaultValue ""
                                    failure <- Some(sprintf "hunk at line %d does not match '%s' (first expected line: %s); read the file again and patch what is there" hunk.oldStart path (if first = "" then "(blank)" else first))
                                | Some at ->
                                    let after = Array.ofList hunk.after
                                    lines <- Array.concat [ lines.[.. at - 1]; after; lines.[at + hunk.before.Length ..] ]
                                    offset <- offset + (after.Length - hunk.before.Length) + (at - (hunk.oldStart - 1 + offset))
                        match failure with
                        | Some e -> toolError e
                        | None ->
                            File.WriteAllText(full, joinWithEnding lines crlf trailing)
                            ok (sprintf "patched '%s': %d hunks applied, %d lines changed" path hunks.Length (patchDelta patch))

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
    /// with sandbox-exec. Linux: the same posture with bubblewrap — the
    /// filesystem mounts read-only, with the workspace and /tmp bound back
    /// writable. Reads and network stay open on both — stated plainly in
    /// docs/security-model.md. Elsewhere (or if the sandbox tool is missing
    /// or unusable) the command runs unconfined and we warn once: approval
    /// is then the only gate.
    let mutable private warnedNoSandbox = false

    /// bwrap needs user namespaces, which some kernels and containers deny;
    /// probe once with a no-op so a broken bwrap degrades to the warning
    /// instead of failing every command.
    let private bwrapPath =
        lazy (
            if not (OperatingSystem.IsLinux()) then None
            else
                [ "/usr/bin/bwrap"; "/usr/local/bin/bwrap"; "/bin/bwrap" ]
                |> List.tryFind File.Exists
                |> Option.bind (fun path ->
                    try
                        use probe = new Process()
                        probe.StartInfo.FileName <- path
                        for arg in [ "--die-with-parent"; "--ro-bind"; "/"; "/"
                                     "--dev"; "/dev"; "--proc"; "/proc"; "/bin/true" ] do
                            probe.StartInfo.ArgumentList.Add arg
                        probe.StartInfo.RedirectStandardOutput <- true
                        probe.StartInfo.RedirectStandardError <- true
                        probe.StartInfo.UseShellExecute <- false
                        probe.Start() |> ignore
                        if probe.WaitForExit 5000 && probe.ExitCode = 0 then Some path
                        else
                            (try probe.Kill true with _ -> ())
                            None
                    with _ -> None))

    /// Whether shell commands on this machine run under bubblewrap.
    /// Internal for tests and diagnostics.
    let internal linuxSandboxActive () =
        OperatingSystem.IsLinux() && (bwrapPath.Value |> Option.isSome)

    /// A host that already confines the whole jern process (a managed
    /// runner with its own mount and network namespaces, for instance) says
    /// so with `JERN_SANDBOX=external`. jern then runs shell commands
    /// directly, records the fact in the trace, and does not warn: the
    /// outer boundary holds, and a nested sandbox would need namespaces the
    /// outer one deliberately denies.
    let externalSandbox () =
        String.Equals(Environment.GetEnvironmentVariable "JERN_SANDBOX", "external", StringComparison.OrdinalIgnoreCase)

    /// What confines shell commands for this process, as the run envelope
    /// records it: `sandbox-exec`, `bubblewrap`, `external`, or `none`.
    let sandboxMode () =
        if externalSandbox () then "external"
        elif OperatingSystem.IsMacOS() && File.Exists "/usr/bin/sandbox-exec" then "sandbox-exec"
        elif linuxSandboxActive () then "bubblewrap"
        else "none"

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

    /// One command run the way `shell` runs it: `/bin/sh -c` (cmd.exe on
    /// Windows) under the OS sandbox when there is one, in the workspace,
    /// with a wall-clock cap. Output and exit code, or the timeout.
    let private runCommand (root: string) (command: string) (timeout: TimeSpan) : Result<string * int * float, string> =
        use proc = new Process()
        let sandboxExec = "/usr/bin/sandbox-exec"
        if externalSandbox () then
            // The host confines the whole process; see externalSandbox.
            proc.StartInfo.FileName <- "/bin/sh"
            proc.StartInfo.ArgumentList.Add "-c"
        elif OperatingSystem.IsMacOS() && File.Exists sandboxExec then
            proc.StartInfo.FileName <- sandboxExec
            proc.StartInfo.ArgumentList.Add "-p"
            proc.StartInfo.ArgumentList.Add(sandboxProfile root)
            proc.StartInfo.ArgumentList.Add "/bin/sh"
            proc.StartInfo.ArgumentList.Add "-c"
        elif OperatingSystem.IsLinux() && (bwrapPath.Value |> Option.isSome) then
            // Everything mounts read-only, then the workspace and /tmp
            // bind back writable — the sandbox-exec posture: writes
            // confined, reads and network open (docs/security-model.md).
            proc.StartInfo.FileName <- bwrapPath.Value.Value
            let rootFull = Path.GetFullPath root
            for arg in [ "--die-with-parent"
                         "--ro-bind"; "/"; "/"
                         "--bind"; rootFull; rootFull
                         "--bind"; "/tmp"; "/tmp"
                         "--dev"; "/dev"
                         "--proc"; "/proc"
                         "/bin/sh"; "-c" ] do
                proc.StartInfo.ArgumentList.Add arg
        else
            if not warnedNoSandbox then
                warnedNoSandbox <- true
                let hint = if OperatingSystem.IsLinux() then " (install bubblewrap to confine writes)" else ""
                eprintfn "jern: no OS sandbox available for shell commands%s; approval is the only gate" hint
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
        try
            let started = Diagnostics.Stopwatch.StartNew()
            proc.Start() |> ignore
            let stdout = proc.StandardOutput.ReadToEndAsync()
            let stderr = proc.StandardError.ReadToEndAsync()
            if proc.WaitForExit(int timeout.TotalMilliseconds) then
                let output =
                    [ stdout.Result; stderr.Result ]
                    |> List.filter (fun s -> s <> "")
                    |> String.concat "\n"
                Ok(output, proc.ExitCode, started.Elapsed.TotalSeconds)
            else
                try proc.Kill(true) with _ -> ()
                Error(sprintf "command timed out after %.0f seconds" timeout.TotalSeconds)
        with ex ->
            Error(sprintf "failed to run command: %s" ex.Message)

    let private shell root input =
        match stringArg "command" input with
        | Error e -> toolError e
        | Ok command ->
            match runCommand root command (TimeSpan.FromSeconds limits.shellTimeoutSeconds) with
            | Error e -> toolError e
            | Ok (output, exitCode, _) ->
                let output = if output = "" then "(no output)" else output
                if exitCode = 0 then ok output
                else toolError (sprintf "%s\n(exit code %d)" output exitCode)

    // -----------------------------------------------------------------------
    // Tests as a tool. The command is the repository's own (jern.json
    // "test_command"), never the model's; a filter or path narrows the run
    // through the flag the runner takes for it, quoted as one argument and
    // limited to characters no shell reads, so nothing rides along. The
    // output comes back parsed (TestReport): failures first, counts, and
    // only as much raw text as the parse left unexplained.

    /// Which runner a test command invokes, for the narrowing flags.
    let private runnerOf (command: string) =
        let c = command.ToLowerInvariant()
        if c.Contains "pytest" then "pytest"
        elif c.Contains "unittest" then "unittest"
        elif c.Contains "dotnet test" then "dotnet"
        elif c.Contains "vitest" then "vitest"
        elif c.Contains "jest" then "jest"
        elif c.Contains "cargo test" then "cargo" // before go: "cargo test" contains "go test"
        elif c.Contains "go test" then "go"
        else ""

    let private safeArgument = Regex(@"^[A-Za-z0-9_.:/~\-\[\]() ,]+$", RegexOptions.Compiled)

    let private quoted (value: string) =
        if OperatingSystem.IsWindows() then "\"" + value + "\"" else "'" + value + "'"

    /// The command line with the filter and path applied, or why they
    /// cannot be.
    let narrowTestCommand (command: string) (filter: string option) (path: string option) : Result<string, string> =
        let runner = runnerOf command
        let checkArgument (label: string) (value: string) =
            if value.Trim() = "" then Error(sprintf "%s must not be empty" label)
            elif value.Length > 200 then Error(sprintf "%s is too long" label)
            elif not (safeArgument.IsMatch value) then
                Error(sprintf "%s may contain letters, digits, spaces, and _ . : / ~ - [ ] ( ) , only" label)
            else Ok value
        let withFilter (cmd: string) =
            match filter with
            | None -> Ok cmd
            | Some f ->
                checkArgument "filter" f
                |> Result.bind (fun f ->
                    match runner with
                    | "pytest" | "unittest" -> Ok(sprintf "%s -k %s" cmd (quoted f))
                    | "dotnet" -> Ok(sprintf "%s --filter %s" cmd (quoted ("FullyQualifiedName~" + f)))
                    | "jest" | "vitest" -> Ok(sprintf "%s -t %s" cmd (quoted f))
                    | "go" -> Ok(sprintf "%s -run %s" cmd (quoted f))
                    | "cargo" -> Ok(sprintf "%s %s" cmd (quoted f))
                    | _ -> Error "filter is not supported for this test command; run the whole suite")
        let withPath (cmd: string) =
            match path with
            | None -> Ok cmd
            | Some p ->
                checkArgument "path" p
                |> Result.bind (fun p ->
                    match runner with
                    | "pytest" | "jest" | "vitest" -> Ok(sprintf "%s %s" cmd (quoted p))
                    | "go" ->
                        let p = p.TrimEnd('/')
                        Ok(sprintf "%s %s" cmd (quoted (if p.StartsWith "./" then p else "./" + p)))
                    | "" -> Error "path is not supported for this test command; run the whole suite"
                    | r -> Error(sprintf "path is not supported for %s; use filter" (if r = "dotnet" then "dotnet test" else r)))
        withFilter command |> Result.bind withPath

    let private runTests root input =
        match optionalStringArg "filter" "" input, optionalStringArg "path" "" input with
        | Error e, _ | _, Error e -> toolError e
        | Ok filter, Ok path ->
            match testCommand with
            | None -> toolError "no test_command is configured in jern.json; there is nothing to run"
            | Some command ->
                let path =
                    if path = "" then Ok None
                    else
                        match resolve root path with
                        | Error e -> Error e
                        | Ok full ->
                            if File.Exists full || Directory.Exists full then
                                Ok(Some(Path.GetRelativePath(Path.GetFullPath root, full).Replace('\\', '/')))
                            else Error(sprintf "path '%s' does not exist" path)
                match path with
                | Error e -> toolError e
                | Ok path ->
                    match narrowTestCommand command (if filter = "" then None else Some filter) path with
                    | Error e -> toolError e
                    | Ok commandLine ->
                        match runCommand root commandLine (TimeSpan.FromSeconds limits.testTimeoutSeconds) with
                        | Error e -> toolError e
                        | Ok (output, exitCode, seconds) ->
                            let report = TestReport.parse output
                            let text = TestReport.render exitCode seconds output report
                            if exitCode = 0 then ok text else toolError text

    // -----------------------------------------------------------------------
    // Git as data. Read-only views over the repository so the model does
    // not spend turns on `git status && git diff && git log` through shell
    // or invent flags: a path under the workspace, a validated ref, and
    // numbers are all it can pass.

    let private maxDiffChars = 24_000

    let private requireRepo root (body: unit -> LispVal) =
        if Git.isRepo root then body () else toolError "the workspace is not a git repository"

    let private optionalPath root input =
        match optionalStringArg "path" "" input with
        | Error e -> Error e
        | Ok "" -> Ok None
        | Ok path ->
            match resolve root path with
            | Error e -> Error e
            | Ok full -> Ok(Some(Path.GetRelativePath(Path.GetFullPath root, full).Replace('\\', '/')))

    let private boolArg key input =
        match plistTryGet key input with
        | Some (Bool b) -> Ok b
        | Some (Keyword "null") | None -> Ok false
        | Some other -> Error(sprintf "argument '%s' must be true or false, got %s" key (showVal other))

    let private intArg key fallback input =
        match plistTryGet key input with
        | Some (Obj v) -> (try Ok(Convert.ToInt32 v) with _ -> Error(sprintf "argument '%s' must be a number" key))
        | Some (Keyword "null") | None -> Ok fallback
        | Some other -> Error(sprintf "argument '%s' must be a number, got %s" key (showVal other))

    let private gitStatus root input =
        requireRepo root (fun () ->
            match Git.status root with
            | Error e -> toolError e
            | Ok (branch, entries) ->
                let describe code =
                    match code with
                    | "M" -> "modified" | "A" -> "added" | "D" -> "deleted" | "R" -> "renamed"
                    | "C" -> "copied" | "U" -> "unmerged" | "T" -> "type changed" | other -> other
                let group title (picked: (string * string) list) =
                    if picked.IsEmpty then []
                    else title :: (picked |> List.truncate limits.maxGrepMatches |> List.map (fun (code, path) -> sprintf "  %s %s" (describe code) path))
                let staged = entries |> List.filter (fun e -> e.staged <> " " && e.staged <> "?") |> List.map (fun e -> e.staged, e.path)
                let unstaged = entries |> List.filter (fun e -> e.unstaged <> " " && e.unstaged <> "?") |> List.map (fun e -> e.unstaged, e.path)
                let untracked = entries |> List.filter (fun e -> e.staged = "?") |> List.map (fun e -> "?", e.path)
                let body =
                    group "Staged:" staged
                    @ group "Unstaged:" unstaged
                    @ (if untracked.IsEmpty then [] else "Untracked:" :: (untracked |> List.truncate limits.maxGrepMatches |> List.map (fun (_, p) -> "  " + p)))
                let head = sprintf "On %s" branch
                ok (String.concat "\n" (head :: (if body.IsEmpty then [ "clean: nothing staged, unstaged, or untracked" ] else body))))

    let private gitDiff root input =
        requireRepo root (fun () ->
            match optionalPath root input, optionalStringArg "ref" "" input, boolArg "staged" input, boolArg "stat" input with
            | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> toolError e
            | Ok path, Ok reference, Ok staged, Ok stat ->
                if reference <> "" && not (Git.isSafeRef reference) then
                    toolError (sprintf "ref '%s' is not a plain ref name" reference)
                else
                    match Git.diff root path (if reference = "" then None else Some reference) staged stat with
                    | Error e -> toolError e
                    | Ok "" ->
                        let what =
                            if staged then "nothing is staged"
                            elif reference <> "" then sprintf "no difference from %s" reference
                            else "no uncommitted changes"
                        ok (sprintf "(%s%s)" what (match path with Some p -> " under " + p | None -> ""))
                    | Ok text ->
                        if text.Length > maxDiffChars then
                            ok (text.Substring(0, maxDiffChars) + sprintf "\n… truncated at %d characters; narrow with path or use stat" maxDiffChars)
                        else ok text)

    let private gitLog root input =
        requireRepo root (fun () ->
            match optionalPath root input, intArg "count" 10 input with
            | Error e, _ | _, Error e -> toolError e
            | Ok path, Ok count ->
                let count = max 1 (min 50 count)
                match Git.log root path count with
                | Error e -> toolError e
                | Ok [] -> ok "(no commits)"
                | Ok entries ->
                    entries
                    |> List.map (fun e ->
                        let files =
                            match e.files with
                            | [] -> ""
                            | fs ->
                                let shown = fs |> List.truncate 10
                                sprintf "\n    %s%s" (String.concat ", " shown) (if fs.Length > 10 then sprintf " … %d more" (fs.Length - 10) else "")
                        sprintf "%s %s %s (%s)%s" e.hash e.date e.subject e.author files)
                    |> String.concat "\n"
                    |> ok)

    let private gitBlame root input =
        requireRepo root (fun () ->
            match stringArg "path" input, intArg "start" 1 input, intArg "end" 0 input with
            | Error e, _, _ | _, Error e, _ | _, _, Error e -> toolError e
            | Ok path, Ok startLine, Ok endLine ->
                match resolve root path with
                | Error e -> toolError e
                | Ok full ->
                    if not (File.Exists full) then toolError (sprintf "file '%s' does not exist" path)
                    else
                        let relative = Path.GetRelativePath(Path.GetFullPath root, full).Replace('\\', '/')
                        let startLine = max 1 startLine
                        let endLine = if endLine <= 0 then startLine + 49 else endLine
                        if endLine < startLine then toolError "end must not be before start"
                        elif endLine - startLine >= 200 then toolError "at most 200 lines at a time"
                        else
                            match Git.blame root relative startLine endLine with
                            | Error e -> toolError e
                            | Ok text -> ok text)

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
                | "symbols" -> Some symbols
                | "outline" -> Some outline
                | "read_symbol" -> Some readSymbol
                | "references" -> Some references
                | "edit_file" -> Some editFile
                | "edit_symbol" -> Some editSymbol
                | "apply_patch" -> Some applyPatch
                | "write_file" -> Some writeFile
                | "shell" -> Some shell
                | "run_tests" -> Some runTests
                | "git_status" -> Some gitStatus
                | "git_diff" -> Some gitDiff
                | "git_log" -> Some gitLog
                | "git_blame" -> Some gitBlame
                | _ -> None
            match run with
            | Some tool ->
                try Choice2Of2(tool root input)
                with ex -> Choice2Of2(toolError (sprintf "%s failed: %s" name ex.Message))
            | None -> Choice2Of2(toolError (sprintf "unknown tool '%s'" name))
        | _ -> Choice1Of2(Default "tool-call payload must have a :name string")
