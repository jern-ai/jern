namespace Jern.Host

open System
open System.Diagnostics

/// Git operations behind the git handler (kernel/handlers.ikr), `jern undo`,
/// and the read-only git tools. Every jern-authored commit carries the
/// author `jern <jern@localhost>`, which is what makes undo safe: it only
/// ever pops a commit jern itself made.
module Git =

    let author = "jern <jern@localhost>"
    let private authorEmail = "jern@localhost"

    let private run (root: string) (args: string list) : Result<string, string> =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        // Identity via -c so commits work in repos (and CI) with no user config.
        for a in [ "-c"; "user.name=jern"; "-c"; "user.email=" + authorEmail ] @ args do
            p.StartInfo.ArgumentList.Add a
        p.StartInfo.WorkingDirectory <- root
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        p.StartInfo.UseShellExecute <- false
        try
            p.Start() |> ignore
            let stdout = p.StandardOutput.ReadToEnd()
            let stderr = p.StandardError.ReadToEnd()
            p.WaitForExit()
            if p.ExitCode = 0 then Ok(stdout.Trim())
            else Error((stdout + stderr).Trim())
        with ex ->
            Error ex.Message

    let isRepo (root: string) =
        match run root [ "rev-parse"; "--is-inside-work-tree" ] with
        | Ok "true" -> true
        | _ -> false

    /// Does `path` carry uncommitted user work — staged, unstaged, or both?
    /// Judged from `git status`, which sees the index as well as the
    /// working tree: a change the user staged but did not commit is theirs
    /// to keep just as much as an unstaged one, and a `diff` against the
    /// index alone would miss it. Untracked files are not dirty — a
    /// brand-new file the agent is about to edit has no user history to save.
    let isFileDirty (root: string) (path: string) =
        match run root [ "status"; "--porcelain"; "--"; path ] with
        | Ok status -> status <> "" && not (status.StartsWith "??")
        | Error _ -> false

    /// Stage and commit just `path`. Returns the new commit hash, or None
    /// when there was nothing to commit.
    let commitPath (root: string) (path: string) (message: string) : string option =
        match run root [ "add"; "--"; path ] with
        | Error _ -> None
        | Ok _ ->
            match run root [ "commit"; "--author"; author; "-m"; message; "--"; path ] with
            | Error _ -> None
            | Ok _ ->
                match run root [ "rev-parse"; "--short"; "HEAD" ] with
                | Ok hash -> Some hash
                | Error _ -> None

    /// The subject of HEAD if jern authored it.
    let headJernCommit (root: string) : string option =
        match run root [ "log"; "-1"; "--format=%ae%n%s" ] with
        | Ok output ->
            match output.Split '\n' with
            | [| email; subject |] when email = authorEmail -> Some subject
            | _ -> None
        | Error _ -> None

    /// Tracked, uncommitted changes anywhere in the tree? A hard reset would
    /// silently discard them. Untracked files are safe — reset leaves them.
    let private hasTrackedChanges (root: string) =
        match run root [ "status"; "--porcelain" ] with
        | Ok status ->
            status.Split '\n'
            |> Array.exists (fun line -> line <> "" && not (line.StartsWith "??"))
        | Error _ -> false

    /// Undo the last jern-authored commit (hard reset by one). Refuses when
    /// HEAD is not jern's, or when the working tree carries uncommitted
    /// changes the reset would destroy — undo only ever removes jern's own
    /// work, never the user's.
    let undoLast (root: string) : Result<string, string> =
        if not (isRepo root) then Error "not a git repository"
        else
            match headJernCommit root with
            | None -> Error "HEAD is not a jern commit; nothing to undo"
            | Some subject ->
                if hasTrackedChanges root then
                    Error "the working tree has uncommitted changes that a hard reset would discard; commit or stash them first"
                else
                    match run root [ "reset"; "--hard"; "HEAD~1" ] with
                    | Ok _ -> Ok subject
                    | Error e -> Error("undo failed: " + e)

    // -----------------------------------------------------------------------
    // Read-only views for the git_* tools. Every argument that reaches git
    // is a path under the workspace, a validated ref, or a number; the
    // model never writes a git command line.

    /// A ref the model may name: no leading dash (no options ride in), and
    /// only the characters refs, `HEAD~3`, `@{u}`, and ranges use.
    let isSafeRef (value: string) =
        value <> ""
        && not (value.StartsWith "-")
        && value |> Seq.forall (fun c -> Char.IsLetterOrDigit c || "_./~^@{}-:".Contains c)
        && not (value.Contains "..")   // ranges are diff's business, not the tool's

    type StatusEntry = { staged: string; unstaged: string; path: string }

    /// Branch line and entries from `git status --porcelain=v1 --branch`.
    let status (root: string) : Result<string * StatusEntry list, string> =
        run root [ "status"; "--porcelain=v1"; "--branch"; "--untracked-files=all" ]
        |> Result.map (fun output ->
            let lines = output.Split('\n') |> Array.filter (fun l -> l <> "")
            let branch =
                lines |> Array.tryFind (fun l -> l.StartsWith "## ") |> Option.map (fun l -> l.Substring 3) |> Option.defaultValue ""
            let entries =
                lines
                |> Array.filter (fun l -> not (l.StartsWith "## ") && l.Length > 3)
                |> Array.map (fun l -> { staged = string l.[0]; unstaged = string l.[1]; path = l.Substring 3 })
                |> List.ofArray
            branch, entries)

    /// `git diff` between the working tree and HEAD (all uncommitted
    /// change), the index (`staged`), or a ref; optionally as `--stat`.
    let diff (root: string) (path: string option) (reference: string option) (staged: bool) (stat: bool) : Result<string, string> =
        let args =
            [ yield "diff"
              yield "--no-color"
              if stat then yield "--stat"
              if staged then yield "--cached"
              match reference with
              | Some r -> yield r
              | None -> if not staged then yield "HEAD"
              yield "--"
              match path with Some p -> yield p | None -> () ]
        run root args

    type LogEntry = { hash: string; date: string; author: string; subject: string; files: string list }

    /// The last `count` commits touching `path` (or anything), newest first.
    let log (root: string) (path: string option) (count: int) : Result<LogEntry list, string> =
        let args =
            [ yield "log"; yield sprintf "-n%d" count; yield "--date=short"; yield "--name-only"
              yield "--format=%x1e%h%x1f%ad%x1f%an%x1f%s"
              yield "--"
              match path with Some p -> yield p | None -> () ]
        run root args
        |> Result.map (fun output ->
            output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun record ->
                let header, files =
                    match record.IndexOf '\n' with
                    | -1 -> record, ""
                    | i -> record.Substring(0, i), record.Substring(i + 1)
                match header.Split '\x1f' with
                | [| hash; date; author; subject |] ->
                    Some { hash = hash; date = date; author = author; subject = subject
                           files = files.Split('\n') |> Array.filter (fun f -> f.Trim() <> "") |> List.ofArray }
                | _ -> None)
            |> List.ofArray)

    /// `git blame` for a line range of one file, short hashes and dates.
    let blame (root: string) (path: string) (startLine: int) (endLine: int) : Result<string, string> =
        run root [ "blame"; "--date=short"; "-L"; sprintf "%d,%d" startLine endLine; "--"; path ]

    /// Tracked and untracked files under `path` that git does not ignore,
    /// workspace-relative with forward slashes. The list `file_tree` shows
    /// inside a repository: what the repository itself considers its files.
    let listFiles (root: string) (path: string) : Result<string list, string> =
        run root [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "--"; path ]
        |> Result.map (fun output ->
            output.Split('\n')
            |> Array.filter (fun l -> l <> "")
            |> Array.map (fun l -> l.Replace('\\', '/'))
            |> List.ofArray)
