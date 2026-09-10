namespace Jern.Host

open System
open System.Text.Json

/// An acceptance run: the workspace's test command executed once, after
/// the agent is done, against the tree it proposes, and read out as data.
/// The command is the protected baseline's when one is given, so an agent
/// that edits `jern.json` cannot change what verifies it. The result is
/// what a receipt can quote beside "policy respected": the command, the
/// counts, the failures by name, and how long it took.
module Verification =

    type Result =
        { /// "passed" or "failed" from the exit code; the report may say more.
          status: string
          command: string
          /// Where the command came from: "baseline" or "jern.json".
          source: string
          exitCode: int
          seconds: float
          report: TestReport.Report
          /// The last part of the output, for a failure the report could not name.
          outputTail: string }

    let private tail (output: string) =
        let limit = 4000
        if output.Length <= limit then output else output.Substring(output.Length - limit)

    /// Runs the command in the workspace root under the same sandbox
    /// `run_tests` uses and parses its output.
    let run (root: string) (command: string) (source: string) (timeout: TimeSpan) : Core.Result<Result, string> =
        match Tools.runTestCommand root command timeout with
        | Error message -> Error message
        | Ok (output, exitCode, seconds) ->
            let report = TestReport.parse output
            Ok
                { status = (if exitCode = 0 then "passed" else "failed")
                  command = command
                  source = source
                  exitCode = exitCode
                  seconds = Math.Round(seconds, 2)
                  report = report
                  outputTail = tail output }

    /// The result as one JSON object, the shape a control plane stores.
    let toJson (result: Result) : string =
        let failures =
            result.report.failures
            |> List.truncate 50
            |> List.map (fun failure -> {| test = failure.test; file = failure.file; line = failure.line; message = failure.message |})
            |> List.toArray
        let counted (value: int option) : obj = match value with Some n -> box n | None -> null
        JsonSerializer.Serialize(
            {| status = result.status
               command = result.command
               source = result.source
               exit_code = result.exitCode
               seconds = result.seconds
               runner = result.report.runner
               passed = counted result.report.passed
               failed = counted result.report.failed
               skipped = counted result.report.skipped
               failures = failures
               output_tail = result.outputTail |})
