/// Tests run one class at a time. Session creation configures process-wide
/// host state (the workspace's test command and limits in Tools), so two
/// classes creating sessions at once can see each other's configuration:
/// a run_tests in one test ran another test's command on CI. The suite
/// takes seconds, so serial is cheap and the fixtures stay simple.
module Jern.Tests.Parallelism

open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
