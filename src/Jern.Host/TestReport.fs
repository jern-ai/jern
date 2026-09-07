namespace Jern.Host

open System
open System.Text.RegularExpressions

/// What a test run said, read out of its output: counts and failures with
/// the file, line, and message the model needs, ahead of the raw text. The
/// runners are recognised from their output, not from the command, so a
/// `make test` that runs pytest still parses. Anything unrecognised keeps
/// the exit code and a tail of the output.
module TestReport =

    type Failure =
        { test: string
          /// "" when the output did not say.
          file: string
          /// 0 when the output did not say.
          line: int
          message: string }

    type Report =
        { runner: string
          passed: int option
          failed: int option
          skipped: int option
          failures: Failure list }

    let private ansi = Regex(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled)

    let private lines (output: string) =
        ansi.Replace(output, "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')

    let private trimMessage (s: string) =
        let t = s.Trim()
        if t.Length > 300 then t.Substring(0, 300) + "…" else t

    let private countOf (pattern: string) (text: string) =
        let m = Regex.Match(text, pattern)
        if m.Success then Some(int m.Groups.[1].Value) else None

    let private slashes (path: string) = path.Replace('\\', '/')

    // -----------------------------------------------------------------------
    // pytest

    let private pytestSummary = Regex(@"^=+ (.+?) in [\d.]+s.*=+$", RegexOptions.Compiled)
    let private pytestSection = Regex(@"^_{3,} (.+?) _{3,}$", RegexOptions.Compiled)
    let private pytestLocation = Regex(@"^([^\s:]+\.py):(\d+): (.+)$", RegexOptions.Compiled)
    let private pytestShort = Regex(@"^(FAILED|ERROR) (\S+?)(?: - (.*))?$", RegexOptions.Compiled)

    let private parsePytest (ls: string[]) =
        let summary = ls |> Array.tryPick (fun l -> let m = pytestSummary.Match l in if m.Success then Some m.Groups.[1].Value else None)
        let count word = summary |> Option.bind (countOf (sprintf @"(\d+) %s" word))
        let failed =
            match count "failed", count "error" with
            | None, None -> None
            | f, e -> Some((defaultArg f 0) + (defaultArg e 0))
        // Sections: "___ test_name ___" … "file.py:12: Error" / "E   detail".
        let sections =
            let mutable current: string option = None
            let mutable location = "", 0, ""
            let mutable detail: string list = []
            let found = Collections.Generic.Dictionary<string, string * int * string>()
            let flush () =
                match current with
                | Some name ->
                    let file, line, msg = location
                    let message =
                        match detail with
                        | [] -> msg
                        | d -> String.Join(" ", List.rev d |> List.truncate 3)
                    found.[name] <- (file, line, message)
                | None -> ()
            for l in ls do
                let m = pytestSection.Match l
                if m.Success then
                    flush ()
                    current <- Some m.Groups.[1].Value
                    location <- "", 0, ""
                    detail <- []
                elif current.IsSome then
                    let loc = pytestLocation.Match l
                    if loc.Success then location <- loc.Groups.[1].Value, int loc.Groups.[2].Value, loc.Groups.[3].Value
                    elif l.StartsWith "E   " then detail <- l.Substring(1).Trim() :: detail
                    elif l.StartsWith "====" || l.StartsWith "----" then
                        flush ()
                        current <- None
            flush ()
            found
        let shortLines =
            ls
            |> Array.choose (fun l ->
                let m = pytestShort.Match l
                if m.Success then Some(m.Groups.[2].Value, m.Groups.[3].Value) else None)
            |> List.ofArray
        let failures =
            match shortLines with
            | [] ->
                sections
                |> Seq.map (fun kv ->
                    let file, line, message = kv.Value
                    { test = kv.Key; file = slashes file; line = line; message = trimMessage message })
                |> List.ofSeq
            | shorts ->
                shorts
                |> List.map (fun (id, short) ->
                    let name = (id.Split("::") |> Array.last)
                    let bare = (name.Split('[') |> Array.head)
                    let file, line, message =
                        match sections.TryGetValue name with
                        | true, v -> v
                        | _ ->
                            match sections |> Seq.tryFind (fun kv -> kv.Key.EndsWith name || kv.Key.EndsWith bare) with
                            | Some kv -> kv.Value
                            | None -> (id.Split("::") |> Array.head), 0, ""
                    let message = if short <> "" then short else message
                    { test = id; file = slashes file; line = line; message = trimMessage message })
        { runner = "pytest"; passed = count "passed"; failed = failed; skipped = count "skipped"; failures = failures }

    // -----------------------------------------------------------------------
    // unittest

    let private unittestHeader = Regex(@"^(FAIL|ERROR): (\S+) \((\S+)\)", RegexOptions.Compiled)
    let private unittestFrame = Regex(@"^\s*File ""(.+?)"", line (\d+), in (\S+)", RegexOptions.Compiled)
    let private unittestRan = Regex(@"^Ran (\d+) tests?", RegexOptions.Compiled)
    let private unittestResult = Regex(@"^FAILED \((.*)\)", RegexOptions.Compiled)

    let private parseUnittest (ls: string[]) =
        let ran = ls |> Array.tryPick (fun l -> let m = unittestRan.Match l in if m.Success then Some(int m.Groups.[1].Value) else None)
        let result = ls |> Array.tryPick (fun l -> let m = unittestResult.Match l in if m.Success then Some m.Groups.[1].Value else None)
        let ok = ls |> Array.exists (fun l -> l.Trim() = "OK" || l.StartsWith "OK (")
        let failedCount =
            match result with
            | Some r -> Some((defaultArg (countOf @"failures=(\d+)" r) 0) + (defaultArg (countOf @"errors=(\d+)" r) 0))
            | None -> if ok then Some 0 else None
        let skipped =
            match result with
            | Some r -> countOf @"skipped=(\d+)" r
            | None -> ls |> Array.tryPick (fun l -> if l.StartsWith "OK (" then countOf @"skipped=(\d+)" l else None)
        let failures = Collections.Generic.List<Failure>()
        let mutable i = 0
        while i < ls.Length do
            let m = unittestHeader.Match ls.[i]
            if m.Success then
                // Python 3.11+ writes "test_x (pkg.Case.test_x)", older
                // versions "test_x (pkg.Case)".
                let name =
                    let qualified = m.Groups.[3].Value
                    if qualified.EndsWith("." + m.Groups.[2].Value) then qualified else qualified + "." + m.Groups.[2].Value
                let mutable file, line = "", 0
                let mutable message = ""
                let mutable j = i + 1
                let mutable stop = false
                while not stop && j < ls.Length do
                    let l = ls.[j]
                    if (l.StartsWith "====" || l.StartsWith "----") && j > i + 1 then stop <- true
                    else
                        let f = unittestFrame.Match l
                        if f.Success then
                            file <- f.Groups.[1].Value
                            line <- int f.Groups.[2].Value
                        elif l.Trim() <> "" && not (l.StartsWith " ") && not (l.StartsWith "Traceback") && not (l.StartsWith "----") then
                            message <- l
                        j <- j + 1
                failures.Add { test = name; file = slashes file; line = line; message = trimMessage message }
                i <- j
            else i <- i + 1
        let failed = match failedCount with Some n -> Some n | None -> if failures.Count > 0 then Some failures.Count else None
        let passed = match ran, failed with Some r, Some f -> Some(max 0 (r - f - defaultArg skipped 0)) | Some r, None -> Some r | _ -> None
        { runner = "unittest"; passed = passed; failed = failed; skipped = skipped; failures = List.ofSeq failures }

    // -----------------------------------------------------------------------
    // dotnet test

    let private dotnetFailed = Regex(@"^\s*Failed (\S+)(?: \[[^\]]*\])?\s*$", RegexOptions.Compiled)
    let private dotnetFrame = Regex(@"\sin (.+?):line (\d+)", RegexOptions.Compiled)
    let private dotnetCounts = Regex(@"Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+)", RegexOptions.Compiled)

    let private parseDotnet (ls: string[]) =
        let counts = ls |> Array.tryPick (fun l -> let m = dotnetCounts.Match l in if m.Success then Some(int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value) else None)
        let failures = Collections.Generic.List<Failure>()
        let mutable i = 0
        while i < ls.Length do
            let m = dotnetFailed.Match ls.[i]
            if m.Success then
                let name = m.Groups.[1].Value
                let mutable message = ""
                let mutable file, line = "", 0
                let mutable j = i + 1
                let mutable inMessage = false
                let mutable stop = false
                while not stop && j < ls.Length do
                    let l = ls.[j]
                    if dotnetFailed.IsMatch l || l.TrimStart().StartsWith "Passed " || l.TrimStart().StartsWith "Skipped " then stop <- true
                    elif l.Trim() = "Error Message:" then inMessage <- true
                    elif l.Trim() = "Stack Trace:" then inMessage <- false
                    elif inMessage && l.Trim() <> "" then
                        message <- (if message = "" then l.Trim() else message + " " + l.Trim())
                    else
                        let f = dotnetFrame.Match l
                        if f.Success && file = "" then
                            file <- f.Groups.[1].Value
                            line <- int f.Groups.[2].Value
                    if not stop then j <- j + 1
                failures.Add { test = name; file = slashes file; line = line; message = trimMessage message }
                i <- j
            else i <- i + 1
        { runner = "dotnet test"
          passed = counts |> Option.map (fun (_, p, _) -> p)
          failed = counts |> Option.map (fun (f, _, _) -> f)
          skipped = counts |> Option.map (fun (_, _, s) -> s)
          failures = List.ofSeq failures }

    // -----------------------------------------------------------------------
    // jest and vitest

    let private jestTests = Regex(@"^Tests:\s+(.+)$", RegexOptions.Compiled)
    let private jestBullet = Regex(@"^\s*● (.+)$", RegexOptions.Compiled)
    let private jsLocation = Regex(@"\(?((?:[A-Za-z]:)?[^\s():]+\.[cm]?[jt]sx?):(\d+):\d+\)?", RegexOptions.Compiled)

    let private parseJest (ls: string[]) =
        let summary = ls |> Array.tryPick (fun l -> let m = jestTests.Match l in if m.Success then Some m.Groups.[1].Value else None)
        let count word = summary |> Option.bind (countOf (sprintf @"(\d+) %s" word))
        let failures = Collections.Generic.List<Failure>()
        let mutable i = 0
        while i < ls.Length do
            let m = jestBullet.Match ls.[i]
            if m.Success && not (m.Groups.[1].Value.StartsWith "Test suite failed") then
                let name = m.Groups.[1].Value.Trim()
                let mutable message = ""
                let mutable file, line = "", 0
                let mutable j = i + 1
                let mutable stop = false
                while not stop && j < ls.Length do
                    let l = ls.[j]
                    if jestBullet.IsMatch l || jestTests.IsMatch l then stop <- true
                    else
                        let loc = jsLocation.Match l
                        if loc.Success && file = "" then
                            file <- loc.Groups.[1].Value
                            line <- int loc.Groups.[2].Value
                        elif message = "" && l.Trim() <> "" && not (l.TrimStart().StartsWith "at ") then message <- l.Trim()
                        j <- j + 1
                // Names carry the seed of a duplicate: skip the "● name" that
                // only repeats a summary bullet.
                if not (failures |> Seq.exists (fun f -> f.test = name)) then
                    failures.Add { test = name; file = slashes file; line = line; message = trimMessage message }
                i <- j
            else i <- i + 1
        { runner = "jest"; passed = count "passed"; failed = count "failed"; skipped = count "skipped"; failures = List.ofSeq failures }

    let private vitestTests = Regex(@"^\s*Tests\s+(.+)$", RegexOptions.Compiled)
    let private vitestFail = Regex(@"^\s*(?:FAIL|×|✗)\s+(\S+\.[cm]?[jt]sx?)(?: > (.+?))?\s*$", RegexOptions.Compiled)

    let private parseVitest (ls: string[]) =
        let summary = ls |> Array.tryPick (fun l -> let m = vitestTests.Match l in if m.Success then Some m.Groups.[1].Value else None)
        let count word = summary |> Option.bind (countOf (sprintf @"(\d+) %s" word))
        let failures = Collections.Generic.List<Failure>()
        let mutable i = 0
        while i < ls.Length do
            let m = vitestFail.Match ls.[i]
            if m.Success && m.Groups.[2].Success then
                let file = m.Groups.[1].Value
                let name = m.Groups.[2].Value.Trim()
                let mutable message = ""
                let mutable line = 0
                let mutable j = i + 1
                let mutable stop = false
                while not stop && j < ls.Length do
                    let l = ls.[j]
                    if vitestFail.IsMatch l || vitestTests.IsMatch l then stop <- true
                    else
                        let loc = jsLocation.Match l
                        if loc.Success && line = 0 && l.Contains file then line <- int loc.Groups.[2].Value
                        elif message = "" && l.Trim() <> "" && not (l.TrimStart().StartsWith "❯") then message <- l.Trim()
                        j <- j + 1
                if not (failures |> Seq.exists (fun f -> f.test = name && f.file = file)) then
                    failures.Add { test = name; file = slashes file; line = line; message = trimMessage message }
                i <- j
            else i <- i + 1
        { runner = "vitest"; passed = count "passed"; failed = count "failed"; skipped = count "skipped"; failures = List.ofSeq failures }

    // -----------------------------------------------------------------------
    // go test

    let private goFail = Regex(@"^\s*--- FAIL: (\S+)", RegexOptions.Compiled)
    let private goPass = Regex(@"^\s*--- PASS: (\S+)", RegexOptions.Compiled)
    let private goSkip = Regex(@"^\s*--- SKIP: (\S+)", RegexOptions.Compiled)
    let private goLocation = Regex(@"^\s+(\S+\.go):(\d+): ?(.*)$", RegexOptions.Compiled)

    let private parseGo (ls: string[]) =
        let failures = Collections.Generic.List<Failure>()
        let mutable i = 0
        while i < ls.Length do
            let m = goFail.Match ls.[i]
            if m.Success then
                let name = m.Groups.[1].Value
                let mutable file, line, message = "", 0, ""
                let mutable j = i + 1
                let mutable stop = false
                while not stop && j < ls.Length do
                    let l = ls.[j]
                    if goFail.IsMatch l || goPass.IsMatch l || l.StartsWith "FAIL" || l.StartsWith "ok" || l.StartsWith "===" then stop <- true
                    else
                        let loc = goLocation.Match l
                        if loc.Success && file = "" then
                            file <- loc.Groups.[1].Value
                            line <- int loc.Groups.[2].Value
                            message <- loc.Groups.[3].Value
                        elif file <> "" && message = "" && l.Trim() <> "" then message <- l.Trim()
                        j <- j + 1
                // With -v the failure text streams before the FAIL line,
                // under "=== RUN"; look back through the indented lines.
                if file = "" then
                    let mutable k = i - 1
                    while k >= 0 && ls.[k].StartsWith " " do
                        let loc = goLocation.Match ls.[k]
                        if loc.Success then
                            file <- loc.Groups.[1].Value
                            line <- int loc.Groups.[2].Value
                            message <- loc.Groups.[3].Value
                        k <- k - 1
                // Subtests report under their parent; keep the leaf.
                if not (failures |> Seq.exists (fun f -> f.test.StartsWith(name + "/"))) then
                    failures.Add { test = name; file = slashes file; line = line; message = trimMessage message }
                i <- j
            else i <- i + 1
        let passes = ls |> Array.filter goPass.IsMatch |> Array.length
        let skips = ls |> Array.filter goSkip.IsMatch |> Array.length
        let verbose = passes > 0 || failures.Count > 0 || skips > 0
        { runner = "go test"
          passed = (if verbose then Some passes else None)
          failed = (if verbose then Some failures.Count else None)
          skipped = (if verbose then Some skips else None)
          failures = List.ofSeq failures }

    // -----------------------------------------------------------------------
    // cargo test

    let private cargoResult = Regex(@"^test result: (?:ok|FAILED)\. (\d+) passed; (\d+) failed; (\d+) ignored", RegexOptions.Compiled)
    let private cargoFailed = Regex(@"^test (\S+) \.\.\. FAILED$", RegexOptions.Compiled)
    let private cargoStdout = Regex(@"^---- (\S+) stdout ----$", RegexOptions.Compiled)
    let private cargoPanic = Regex(@"panicked at (\S+?):(\d+):\d+:?\s*(.*)$", RegexOptions.Compiled)

    let private parseCargo (ls: string[]) =
        // Several result lines (unit, integration, doc tests) add up.
        let results = ls |> Array.choose (fun l -> let m = cargoResult.Match l in if m.Success then Some(int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value) else None)
        let sum pick = if results.Length = 0 then None else Some(results |> Array.sumBy pick)
        let details = Collections.Generic.Dictionary<string, string * int * string>()
        let mutable i = 0
        while i < ls.Length do
            let m = cargoStdout.Match ls.[i]
            if m.Success then
                let name = m.Groups.[1].Value
                let mutable file, line, message = "", 0, ""
                let mutable j = i + 1
                let mutable stop = false
                while not stop && j < ls.Length do
                    let l = ls.[j]
                    if cargoStdout.IsMatch l || l.StartsWith "failures:" || l.StartsWith "test result:" then stop <- true
                    else
                        let p = cargoPanic.Match l
                        if p.Success then
                            file <- p.Groups.[1].Value
                            line <- int p.Groups.[2].Value
                            message <- p.Groups.[3].Value
                        elif file <> "" && message = "" && l.Trim() <> "" && not (l.StartsWith "note:") then message <- l.Trim()
                        j <- j + 1
                details.[name] <- (file, line, message)
                i <- j
            else i <- i + 1
        let names = ls |> Array.choose (fun l -> let m = cargoFailed.Match l in if m.Success then Some m.Groups.[1].Value else None) |> Array.distinct
        let failures =
            names
            |> Array.map (fun name ->
                let file, line, message = match details.TryGetValue name with | true, v -> v | _ -> "", 0, ""
                { test = name; file = slashes file; line = line; message = trimMessage message })
            |> List.ofArray
        { runner = "cargo test"; passed = sum (fun (p, _, _) -> p); failed = sum (fun (_, f, _) -> f); skipped = sum (fun (_, _, s) -> s); failures = failures }

    // -----------------------------------------------------------------------

    /// Read a run's output. The runner is recognised from what it printed.
    let parse (output: string) : Report =
        let ls = lines output
        let has (pattern: string) = ls |> Array.exists (fun l -> Regex.IsMatch(l, pattern))
        if has @"^test result: (ok|FAILED)\." then parseCargo ls
        elif has @"(Passed!|Failed!)\s+-\s+Failed:" || has @"^Total tests:" then parseDotnet ls
        elif has @"^=+ .*(passed|failed|error|no tests ran).* in [\d.]+s" || has @"short test summary info" then parsePytest ls
        elif has @"^Ran \d+ tests? in" then parseUnittest ls
        elif has @"^Tests:\s+\d+" then parseJest ls
        elif has @"^\s*Test Files\s+\d+" then parseVitest ls
        elif has @"^--- (FAIL|PASS|SKIP): " || has @"^(ok|FAIL)\s+\S+\s+[\d.]+s" || has @"^FAIL\s+\S+ \[build failed\]" then parseGo ls
        else { runner = ""; passed = None; failed = None; skipped = None; failures = [] }

    let private maxFailures = 20
    let private tailLines = 40
    let private tailChars = 4000

    /// The last lines of the output, for what the parse did not carry.
    let tail (output: string) =
        let ls = lines (output.TrimEnd())
        let shown = if ls.Length > tailLines then ls.[ls.Length - tailLines ..] else ls
        let text = String.Join("\n", shown)
        let text = if text.Length > tailChars then "…" + text.Substring(text.Length - tailChars) else text
        if ls.Length > tailLines then sprintf "… %d earlier lines omitted\n%s" (ls.Length - tailLines) text else text

    /// Failures first, then counts, then only as much raw output as the
    /// parse left unexplained.
    let render (exitCode: int) (seconds: float) (output: string) (report: Report) : string =
        let counts =
            [ report.failed |> Option.map (sprintf "%d failed")
              report.passed |> Option.map (sprintf "%d passed")
              report.skipped |> Option.bind (fun s -> if s > 0 then Some(sprintf "%d skipped" s) else None) ]
            |> List.choose id
        let runner = if report.runner = "" then "runner not recognised" else report.runner
        let verdict = if exitCode = 0 then "PASSED" else "FAILED"
        let headline =
            sprintf "%s: %s(%s, exit %d, %.1fs)" verdict
                (if counts.IsEmpty then "" else String.Join(", ", counts) + " ")
                runner exitCode seconds
        let failureLines =
            report.failures
            |> List.truncate maxFailures
            |> List.map (fun f ->
                let where =
                    if f.file = "" then ""
                    elif f.line = 0 then f.file + ": "
                    else sprintf "%s:%d: " f.file f.line
                let message = if f.message = "" then "" else " — " + f.message
                sprintf "%s%s%s" where f.test message)
        let more =
            if report.failures.Length > maxFailures then [ sprintf "… %d more failures" (report.failures.Length - maxFailures) ] else []
        // A recognised runner's pass needs no output; an unrecognised one
        // explained nothing, so its tail always rides along.
        let explained = report.runner <> "" && (exitCode = 0 || (not report.failures.IsEmpty && report.failed.IsSome))
        let rawTail =
            if explained then []
            elif output.Trim() = "" then [ "(no output)" ]
            else [ ""; "[output]"; tail output ]
        String.Join("\n", headline :: failureLines @ more @ rawTail)
