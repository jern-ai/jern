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
          /// Wall-clock cap on one kernel_eval program (model-authored
          /// Kernel code has no other stop for a pure loop).
          evalTimeoutSeconds: float }

    let defaultLimits =
        { maxFileBytes = 262_144L
          maxGrepMatches = 200
          maxTreeEntries = 200
          shellTimeoutSeconds = 120.0
          evalTimeoutSeconds = 30.0 }

    let mutable private limits = defaultLimits
    let configureLimits (value: Limits) = limits <- value
    let currentLimits () = limits

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

    let private shell root input =
        match stringArg "command" input with
        | Error e -> toolError e
        | Ok command ->
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
                | "symbols" -> Some symbols
                | "outline" -> Some outline
                | "read_symbol" -> Some readSymbol
                | "references" -> Some references
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
