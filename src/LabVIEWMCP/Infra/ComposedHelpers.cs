using System.Security.Cryptography;
using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The server's helper VIs (scripts\*.xml) as the pylabview backend runs them: COMPOSED, not built
/// by NI's converter, and run over ActiveX rather than RunVIAsTopLevel.
///
/// Measured 2026-10-10 (experiments/aixml-without-grpc/FINDINGS.md §7a): over the 81 documents the
/// composer builds 9 with the parts as committed and 31 with parts harvested from the NI-built
/// helpers, and six read-only ones gave the same outputs as NI's. A helper the composer refuses is
/// refused here BY NAME, so a tool on this backend stops at the step that needs it instead of
/// running something else.
///
/// THEIR OWN FOLDER, NOT NI's. NI's helpers live in %TEMP%\LabVIEWMCP\helpers and run in the AI
/// add-on's application instance; these run over ActiveX in LabVIEW's main instance, so the same
/// VI names do not meet in memory. A helper is rebuilt when its AIXML is newer, or when the parts
/// it was composed from are not the parts in use now - the sidecar `.parts` remembers which.
/// </summary>
internal static class ComposedHelpers
{
    public static string Folder => Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers-pylabview");

    /// <summary>The composed helper for <paramref name="aixmlFileName"/>, or null with the reason.</summary>
    public static async Task<(string? Vi, string? Problem)> EnsureAsync(
        string aixmlFileName, int timeoutSeconds, CancellationToken ct)
    {
        var (vi, problem, _) = await EnsureReportingAsync(aixmlFileName, timeoutSeconds, ct);
        return (vi, problem);
    }

    /// <summary>
    /// <see cref="EnsureAsync"/>, also saying whether THIS call composed the helper - what a tool
    /// answers as <c>helperGenerated</c>. Every pylabview route answered false until 2026-11-15,
    /// also when it had just recomposed a stale helper.
    /// </summary>
    public static async Task<(string? Vi, string? Problem, bool Composed)> EnsureReportingAsync(
        string aixmlFileName, int timeoutSeconds, CancellationToken ct)
    {
        if (StatusTools.ScriptsDirectory() is not { } scripts
            || !File.Exists(Path.Combine(scripts, aixmlFileName)))
            return (null, $"{aixmlFileName} was not found in the scripts folder beside the server.", false);
        return await EnsurePathReportingAsync(Path.Combine(scripts, aixmlFileName), null, timeoutSeconds, ct);
    }

    /// <summary>
    /// The same for an AIXML file ANYWHERE - a tool's `helperAixmlPath` override, or a document a
    /// tool wrote itself. The VI goes to <paramref name="viName"/> (default: the document's name)
    /// in <see cref="Folder"/>, and is rebuilt on the same rules.
    /// </summary>
    public static async Task<(string? Vi, string? Problem)> EnsurePathAsync(
        string aixmlPath, string? viName, int timeoutSeconds, CancellationToken ct)
    {
        var (vi, problem, _) = await EnsurePathReportingAsync(aixmlPath, viName, timeoutSeconds, ct);
        return (vi, problem);
    }

    /// <summary><see cref="EnsurePathAsync"/>, also saying whether this call composed the helper.</summary>
    public static async Task<(string? Vi, string? Problem, bool Composed)> EnsurePathReportingAsync(
        string aixmlPath, string? viName, int timeoutSeconds, CancellationToken ct)
    {
        var aixml = Path.GetFullPath(aixmlPath);
        if (!File.Exists(aixml)) return (null, $"No helper AIXML at '{aixml}'.", false);
        if (AixmlComposer.Locate(out var missing) is not { } setup)
            return (null, missing, false);
        var scripts = StatusTools.ScriptsDirectory() ?? Path.GetDirectoryName(aixml)!;

        // in an ActiveX lane (lvai_layout_diagrams' workers) the helper is the lane's own copy: one
        // helper VI in memory, its controls written by two workers at once, read the other's answers
        var lane = LabViewCom.Lane.Value;
        var vi = Path.Combine(Folder, viName ?? (lane is null
            ? Path.ChangeExtension(Path.GetFileName(aixml), ".vi")
            : $"{Path.GetFileNameWithoutExtension(aixml)} {lane}.vi"));
        var stamp = vi + ".parts";
        // a helper composed for one LabVIEW release is not one for another (Error 1125 in 2025)
        var partsInUse = ExpectedStamp(PartsStamp(setup.PartsDirectory) + "|" +
                                       AixmlComposer.TargetRelease(AixmlBackend.LabViewInUse(),
                                                                   AixmlComposer.PartsRelease(setup.PartsDirectory)),
                                       aixml);
        if (!IsStale(aixml, vi, stamp, partsInUse)) return (vi, null, false);

        Directory.CreateDirectory(Folder);
        // ONE COMPOSE PER HELPER AT A TIME. The background pre-warm (HelperPrewarm) and a tool
        // call can want the same helper in the same second, and so can the servers of two
        // sessions started together; two composers writing one file leave neither's VI. Whoever
        // comes second waits and then finds the helper current.
        using var gate = await ComposeGate.EnterAsync(vi, timeoutSeconds, ct);
        if (!IsStale(aixml, vi, stamp, partsInUse)) return (vi, null, false);
        var composed = await AixmlComposer.ComposeAsync(setup, aixml, vi, [Folder, scripts],
                                                        timeoutSeconds, ct);
        if (!composed.Written)
            return (null, $"the pylabview composer {(composed.Refused ? "refuses" : "failed on")} " +
                          $"{Path.GetFileName(aixml)}: {composed.Message}", false);
        await File.WriteAllTextAsync(stamp, partsInUse, ct);
        return (vi, null, true);
    }

    /// <summary>
    /// The stamp a current helper carries: the parts and release it was composed for, and a hash of
    /// its AIXML's CONTENT. By timestamp alone an older AIXML restored with its own older timestamp
    /// - a copy that keeps it - left the helper composed from the newer one in place. Composing
    /// carries none of the risk that keeps the gRPC route's rule narrow (HelperCache): no
    /// ValidateAIXML runs, so a content change rebuilds whatever its timestamp says.
    /// </summary>
    internal static string ExpectedStamp(string partsAndRelease, string aixmlPath) =>
        partsAndRelease + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(aixmlPath)))[..16];

    /// <summary>Rebuild when the VI is missing or too small, its AIXML is newer, or the stamp it was
    /// composed under is not <paramref name="expected"/>.</summary>
    internal static bool IsStale(string aixmlPath, string viPath, string stampPath, string expected) =>
        !File.Exists(viPath) || HelperCache.NeedsRebuild(aixmlPath, viPath)
        || !File.Exists(stampPath) || File.ReadAllText(stampPath) != expected;

    /// <summary>
    /// The lock one compose of one helper holds: a semaphore for this process and a lock FILE
    /// (`<vi>.lock`, opened with no sharing, deleted on close) for the other servers on the
    /// machine. It FAILS OPEN: when the file cannot be had within the budget the compose runs
    /// anyway, as it always did before there was a lock.
    /// </summary>
    internal sealed class ComposeGate : IDisposable
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Local =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _local;
        private readonly FileStream? _file;

        private ComposeGate(SemaphoreSlim local, FileStream? file) => (_local, _file) = (local, file);

        /// <summary>True when the lock file is held - false after a fail-open.</summary>
        internal bool HoldsFile => _file is not null;

        internal static async Task<ComposeGate> EnterAsync(string viPath, int timeoutSeconds, CancellationToken ct)
        {
            var local = Local.GetOrAdd(Path.GetFullPath(viPath), _ => new SemaphoreSlim(1, 1));
            await local.WaitAsync(ct);
            try
            {
                var until = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
                while (true)
                {
                    try
                    {
                        return new ComposeGate(local, new FileStream(viPath + ".lock", FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
                    }
                    catch (IOException) when (DateTime.UtcNow < until)
                    {
                        await Task.Delay(200, ct);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return new ComposeGate(local, null);
                    }
                }
            }
            catch
            {
                local.Release();
                throw;
            }
        }

        public void Dispose()
        {
            _file?.Dispose();
            _local.Release();
        }
    }

    /// <summary>Which parts, and of when - a changed table or a different folder rebuilds the helper.</summary>
    private static string PartsStamp(string partsDirectory)
    {
        var table = Path.Combine(partsDirectory, "vi-server-props.json");
        return Path.GetFullPath(partsDirectory) + "|" +
               (File.Exists(table) ? File.GetLastWriteTimeUtc(table).Ticks : 0);
    }
}
