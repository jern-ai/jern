namespace Jern.Host

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

/// First-use trust for workspace policy files (.jern/policy.ikr).
///
/// A workspace policy is evaluated in the privileged handler environment, so
/// merely cloning a repository must not be enough to run its policy
/// (docs/security-model.md). Front-ends ask the user before a session loads
/// an unseen policy and remember yes answers here:
/// `~/.config/jern/trusted.json` holds `{ "<absolute path>": "<sha256 hex>" }`
/// with 0600 permissions — the same conventions as credentials.json. The hash
/// pins the exact content that was approved, so a changed file asks again.
module Trust =

    /// The trust store location. JERN_CONFIG_DIR overrides the directory
    /// (sandboxes and scripted setups point it somewhere disposable).
    let defaultStorePath () =
        let configDir =
            match Environment.GetEnvironmentVariable "JERN_CONFIG_DIR" with
            | null | "" ->
                Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                             ".config", "jern")
            | dir -> dir
        Path.Combine(configDir, "trusted.json")

    let contentHash (content: string) =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content: string))).ToLowerInvariant()

    /// The store's entries; unreadable or malformed files count as empty
    /// (nothing trusted) rather than an error.
    let private readStore (storePath: string) : JsonObject =
        if File.Exists storePath then
            try
                match JsonNode.Parse(File.ReadAllText storePath) with
                | :? JsonObject as o -> o
                | _ -> JsonObject()
            with _ -> JsonObject()
        else JsonObject()

    /// Has exactly this content at this path been trusted before?
    let isTrusted (storePath: string) (policyPath: string) (content: string) =
        match (readStore storePath).[Path.GetFullPath policyPath] with
        | null -> false
        | v -> (try v.GetValue<string>() = contentHash content with _ -> false)

    /// Record this content as trusted, merging with the store's other entries
    /// and replacing any earlier hash for the same path.
    let remember (storePath: string) (policyPath: string) (content: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath storePath)) |> ignore
        let store = readStore storePath
        store.[Path.GetFullPath policyPath] <- JsonValue.Create(contentHash content)
        File.WriteAllText(storePath, store.ToJsonString())
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(storePath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
