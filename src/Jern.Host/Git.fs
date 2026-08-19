namespace Jern.Host

open System.Diagnostics

/// Git operations behind the git handler (kernel/handlers.ikr) and
/// `jern undo`. Every jern-authored commit carries the author
/// `jern <jern@localhost>`, which is what makes undo safe: it only ever
/// pops a commit jern itself made.
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

    /// Does `path` have uncommitted changes (tracked modifications only —
    /// a brand-new file the agent is about to edit has no user history to save)?
    let isFileDirty (root: string) (path: string) =
        match run root [ "diff"; "--quiet"; "--"; path ] with
        | Ok _ -> false
        | Error _ ->
            // diff --quiet exits 1 when there are changes; distinguish that
            // from a real failure by asking again with output.
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
    let headIronCommit (root: string) : string option =
        match run root [ "log"; "-1"; "--format=%ae%n%s" ] with
        | Ok output ->
            match output.Split '\n' with
            | [| email; subject |] when email = authorEmail -> Some subject
            | _ -> None
        | Error _ -> None

    /// Undo the last jern-authored commit (hard reset by one). Refuses when
    /// HEAD is not jern's, so it can never destroy the user's own work.
    let undoLast (root: string) : Result<string, string> =
        if not (isRepo root) then Error "not a git repository"
        else
            match headIronCommit root with
            | None -> Error "HEAD is not a jern commit; nothing to undo"
            | Some subject ->
                match run root [ "reset"; "--hard"; "HEAD~1" ] with
                | Ok _ -> Ok subject
                | Error e -> Error("undo failed: " + e)
