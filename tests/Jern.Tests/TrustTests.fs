module Jern.Tests.TrustTests

open System
open System.IO
open Xunit
open Jern.Host

/// Every test writes its store under a fresh temp directory — the real
/// ~/.config/jern/trusted.json is never touched.
let private newStore () =
    let dir = Path.Combine(Path.GetTempPath(), "jern-trust-" + Guid.NewGuid().ToString("N"))
    Path.Combine(dir, "trusted.json")

let private cleanup (store: string) =
    Directory.Delete(Path.GetDirectoryName store, true)

[<Fact>]
let ``remembered content is trusted; changed content asks again`` () =
    let store = newStore ()
    try
        let path = Path.Combine(Path.GetTempPath(), "ws", ".jern", "policy.ikr")
        Assert.False(Trust.isTrusted store path "(define x 1)")
        Trust.remember store path "(define x 1)"
        Assert.True(Trust.isTrusted store path "(define x 1)")
        // A changed file is a new decision…
        Assert.False(Trust.isTrusted store path "(define x 2)")
        // …and re-remembering pins the new content, dropping the old.
        Trust.remember store path "(define x 2)"
        Assert.True(Trust.isTrusted store path "(define x 2)")
        Assert.False(Trust.isTrusted store path "(define x 1)")
    finally
        cleanup store

[<Fact>]
let ``trust is keyed by path — workspaces do not clobber each other`` () =
    let store = newStore ()
    try
        let a = Path.Combine(Path.GetTempPath(), "a", ".jern", "policy.ikr")
        let b = Path.Combine(Path.GetTempPath(), "b", ".jern", "policy.ikr")
        Trust.remember store a "(define x 1)"
        Assert.False(Trust.isTrusted store b "(define x 1)")
        Trust.remember store b "(define y 2)"
        Assert.True(Trust.isTrusted store a "(define x 1)")
        Assert.True(Trust.isTrusted store b "(define y 2)")
    finally
        cleanup store

[<Fact>]
let ``a corrupt store trusts nothing and heals on the next remember`` () =
    let store = newStore ()
    try
        Directory.CreateDirectory(Path.GetDirectoryName store) |> ignore
        File.WriteAllText(store, "not json at all")
        let path = Path.Combine(Path.GetTempPath(), "ws", ".jern", "policy.ikr")
        Assert.False(Trust.isTrusted store path "(define x 1)")
        Trust.remember store path "(define x 1)"
        Assert.True(Trust.isTrusted store path "(define x 1)")
    finally
        cleanup store

[<Fact>]
let ``the store file is private to the user`` () =
    if not (OperatingSystem.IsWindows()) then
        let store = newStore ()
        try
            Trust.remember store "/ws/.jern/policy.ikr" "(define x 1)"
            Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite,
                         File.GetUnixFileMode store)
        finally
            cleanup store
