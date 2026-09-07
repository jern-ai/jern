module Jern.Tests.TestReportTests

open Xunit
open Jern.Host

let private only (report: TestReport.Report) =
    Assert.Equal(1, report.failures.Length)
    report.failures.Head

[<Fact>]
let ``pytest failures carry the file, line, and assertion`` () =
    let output = """============================= test session starts ==============================
platform darwin -- Python 3.12.1, pytest-8.0.0, pluggy-1.4.0
rootdir: /repo
collected 3 items

tests/test_math.py .F.                                                   [100%]

=================================== FAILURES ===================================
__________________________________ test_add ___________________________________

    def test_add():
>       assert add(1, 2) == 4
E       assert 3 == 4
E        +  where 3 = add(1, 2)

tests/test_math.py:7: AssertionError
=========================== short test summary info ============================
FAILED tests/test_math.py::test_add - assert 3 == 4
========================= 1 failed, 2 passed in 0.05s ==========================
"""
    let report = TestReport.parse output
    Assert.Equal("pytest", report.runner)
    Assert.Equal(Some 1, report.failed)
    Assert.Equal(Some 2, report.passed)
    let f = only report
    Assert.Equal("tests/test_math.py::test_add", f.test)
    Assert.Equal("tests/test_math.py", f.file)
    Assert.Equal(7, f.line)
    Assert.Equal("assert 3 == 4", f.message)
    let rendered = TestReport.render 1 0.05 output report
    Assert.StartsWith("FAILED: 1 failed, 2 passed (pytest, exit 1, 0.1s)\ntests/test_math.py:7: tests/test_math.py::test_add — assert 3 == 4", rendered)
    // Explained failures do not drag the raw output along.
    Assert.DoesNotContain("[output]", rendered)

[<Fact>]
let ``unittest failures are read from the traceback`` () =
    let output = """F..
======================================================================
FAIL: test_add (tests.test_math.MathTests.test_add)
----------------------------------------------------------------------
Traceback (most recent call last):
  File "/repo/tests/test_math.py", line 7, in test_add
    self.assertEqual(add(1, 2), 4)
AssertionError: 3 != 4

----------------------------------------------------------------------
Ran 3 tests in 0.001s

FAILED (failures=1)
"""
    let report = TestReport.parse output
    Assert.Equal("unittest", report.runner)
    Assert.Equal(Some 1, report.failed)
    Assert.Equal(Some 2, report.passed)
    let f = only report
    Assert.Equal("tests.test_math.MathTests.test_add", f.test)
    Assert.Equal("/repo/tests/test_math.py", f.file)
    Assert.Equal(7, f.line)
    Assert.Equal("AssertionError: 3 != 4", f.message)
    let green = TestReport.parse "...\n----------------------------------------------------------------------\nRan 3 tests in 0.001s\n\nOK\n"
    Assert.Equal(Some 0, green.failed)
    Assert.Equal(Some 3, green.passed)

[<Fact>]
let ``dotnet test failures carry the message and the frame`` () =
    let output = """  Failed MathTests.Add [12 ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: 4
Actual:   3
  Stack Trace:
     at Tests.MathTests.Add() in /repo/tests/MathTests.fs:line 9

Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 20 ms - Tests.dll (net10.0)
"""
    let report = TestReport.parse output
    Assert.Equal("dotnet test", report.runner)
    Assert.Equal((Some 1, Some 2, Some 0), (report.failed, report.passed, report.skipped))
    let f = only report
    Assert.Equal("MathTests.Add", f.test)
    Assert.Equal("/repo/tests/MathTests.fs", f.file)
    Assert.Equal(9, f.line)
    Assert.Equal("Assert.Equal() Failure: Values differ Expected: 4 Actual:   3", f.message)

[<Fact>]
let ``jest failures carry the expectation and the source line`` () =
    let output = """ FAIL  src/math.test.ts
  ● add › adds numbers

    expect(received).toBe(expected) // Object.is equality

    Expected: 4
    Received: 3

      5 |   it("adds numbers", () => {
    > 6 |     expect(add(1, 2)).toBe(4);
        |                       ^

      at Object.<anonymous> (src/math.test.ts:6:23)

Tests:       1 failed, 2 passed, 3 total
"""
    let report = TestReport.parse output
    Assert.Equal("jest", report.runner)
    Assert.Equal((Some 1, Some 2), (report.failed, report.passed))
    let f = only report
    Assert.Equal("add › adds numbers", f.test)
    Assert.Equal("src/math.test.ts", f.file)
    Assert.Equal(6, f.line)
    Assert.Equal("expect(received).toBe(expected) // Object.is equality", f.message)

[<Fact>]
let ``vitest failures are read from the FAIL block`` () =
    let output = """ ❯ src/math.test.ts (3 tests | 1 failed) 12ms
   × add > adds numbers
     → expected 3 to be 4 // Object.is equality

 FAIL  src/math.test.ts > add > adds numbers
AssertionError: expected 3 to be 4 // Object.is equality
 ❯ src/math.test.ts:6:23

 Test Files  1 failed (1)
      Tests  1 failed | 2 passed (3)
"""
    let report = TestReport.parse output
    Assert.Equal("vitest", report.runner)
    Assert.Equal((Some 1, Some 2), (report.failed, report.passed))
    let f = only report
    Assert.Equal("add > adds numbers", f.test)
    Assert.Equal("src/math.test.ts", f.file)
    Assert.Equal(6, f.line)
    Assert.Equal("AssertionError: expected 3 to be 4 // Object.is equality", f.message)

[<Fact>]
let ``go test failures are found whether the text streams before or after the FAIL line`` () =
    let verbose = """=== RUN   TestAdd
    math_test.go:9: add(1, 2) = 3, want 4
--- FAIL: TestAdd (0.00s)
=== RUN   TestSub
--- PASS: TestSub (0.00s)
FAIL
FAIL	example.com/m	0.004s
"""
    let report = TestReport.parse verbose
    Assert.Equal("go test", report.runner)
    Assert.Equal((Some 1, Some 1), (report.failed, report.passed))
    let f = only report
    Assert.Equal("TestAdd", f.test)
    Assert.Equal("math_test.go", f.file)
    Assert.Equal(9, f.line)
    Assert.Equal("add(1, 2) = 3, want 4", f.message)
    let quiet = "--- FAIL: TestAdd (0.00s)\n    math_test.go:9: add(1, 2) = 3, want 4\nFAIL\nFAIL\texample.com/m\t0.004s\n"
    let f2 = only (TestReport.parse quiet)
    Assert.Equal(("math_test.go", 9), (f2.file, f2.line))

[<Fact>]
let ``cargo test failures carry the panic site`` () =
    let output = """running 2 tests
test tests::sub ... ok
test tests::add ... FAILED

failures:

---- tests::add stdout ----
thread 'tests::add' panicked at src/lib.rs:12:9:
assertion `left == right` failed
  left: 3
 right: 4
note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace


failures:
    tests::add

test result: FAILED. 1 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s
"""
    let report = TestReport.parse output
    Assert.Equal("cargo test", report.runner)
    Assert.Equal((Some 1, Some 1), (report.failed, report.passed))
    let f = only report
    Assert.Equal("tests::add", f.test)
    Assert.Equal("src/lib.rs", f.file)
    Assert.Equal(12, f.line)
    Assert.Equal("assertion `left == right` failed", f.message)

[<Fact>]
let ``an unrecognised runner keeps the exit code and a tail of the output`` () =
    let output = String.concat "\n" [ for i in 1 .. 60 -> sprintf "line %d" i ]
    let report = TestReport.parse output
    Assert.Equal("", report.runner)
    let rendered = TestReport.render 2 1.0 output report
    Assert.StartsWith("FAILED: (runner not recognised, exit 2, 1.0s)\n\n[output]\n… 20 earlier lines omitted\nline 21\n", rendered)
    Assert.EndsWith("line 60", rendered)
    // Unrecognised, the tail rides along even on a pass; recognised, a pass
    // is just its counts.
    Assert.StartsWith("PASSED: (runner not recognised, exit 0, 1.0s)\n\n[output]\n", TestReport.render 0 1.0 output report)
    let green = TestReport.parse "...\n----------------------------------------------------------------------\nRan 3 tests in 0.001s\n\nOK\n"
    Assert.Equal("PASSED: 0 failed, 3 passed (unittest, exit 0, 0.2s)", TestReport.render 0 0.2 "…" green)

[<Fact>]
let ``a filter or path rides the runner's own flag as one quoted argument`` () =
    let narrow = Tools.narrowTestCommand
    Assert.Equal(Ok "pytest -q -k 'test_add'", narrow "pytest -q" (Some "test_add") None)
    Assert.Equal(Ok "pytest -q 'tests/test_math.py'", narrow "pytest -q" None (Some "tests/test_math.py"))
    Assert.Equal(Ok "python3 -m unittest discover -s tests -t . -k 'MathTests'", narrow "python3 -m unittest discover -s tests -t ." (Some "MathTests") None)
    Assert.Equal(Ok "dotnet test Jern.slnx --filter 'FullyQualifiedName~ToolTests'", narrow "dotnet test Jern.slnx" (Some "ToolTests") None)
    Assert.Equal(Ok "npx jest -t 'adds numbers' 'src/math.test.ts'", narrow "npx jest" (Some "adds numbers") (Some "src/math.test.ts"))
    Assert.Equal(Ok "go test ./... -run 'TestAdd' './pkg'", narrow "go test ./..." (Some "TestAdd") (Some "pkg/"))
    Assert.Equal(Ok "cargo test 'tests::add'", narrow "cargo test" (Some "tests::add") None)
    // Nothing a shell reads gets through, and runners without a flag say so.
    match narrow "pytest" (Some "x'; rm -rf /") None with
    | Error message -> Assert.Contains("only", message)
    | Ok c -> failwithf "accepted %s" c
    match narrow "make test" (Some "x") None with
    | Error message -> Assert.Contains("not supported", message)
    | Ok c -> failwithf "accepted %s" c
    match narrow "dotnet test" None (Some "tests") with
    | Error message -> Assert.Contains("use filter", message)
    | Ok c -> failwithf "accepted %s" c
