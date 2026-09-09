using System;
using System.Threading;
using System.Threading.Tasks;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Generic push-then-confirm loop shared by every setting that needs to know
/// whether a push actually stuck and retry it if not — extracted from what
/// used to be TrayApp's Rate/HDR-only SettleAsync so G-SYNC's enforcement
/// path (see TrayApp.EnforceGsyncAsync) gets the same confirm-and-retry
/// treatment instead of a second hand-written copy of this exact shape.
///
/// Callers own the actual push mechanics (ordering, what "drifted" means,
/// how long to wait) — this only owns the generic poll/retry/timeout shape,
/// since that's the part that's genuinely identical across settings. G-SYNC's
/// DRS session write is far more expensive (and collision-prone — see
/// NvidiaGsyncService remarks) than a display API call, so it's expected to
/// pass a coarser windowMs/pollMs than Rate/HDR's.
/// </summary>
internal static class SettleLoop
{
    /// <summary>
    /// Assumes the caller already issued the initial push before calling this —
    /// polls `isDrifted` every `pollMs` (via `delay`, real Task.Delay unless a
    /// test substitutes one) for up to `windowMs`, calling `push` again on each
    /// mismatch, until it converges (isDrifted returns false) or the window
    /// runs out. Returns whether it ultimately converged. A cancelled token
    /// (superseded by a newer push) always returns false without checking or
    /// pushing again — same "someone else is already handling this" treatment
    /// the original SettleAsync used.
    /// </summary>
    public static async Task<bool> RunAsync(
        Func<bool> isDrifted,
        Action push,
        CancellationToken token,
        int windowMs,
        int pollMs,
        Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        int elapsed = 0;

        while (elapsed < windowMs)
        {
            try { await delay(pollMs, token); }
            catch (OperationCanceledException) { return false; }
            if (token.IsCancellationRequested) return false;
            elapsed += pollMs;

            if (!isDrifted()) return true;
            push();
        }

        return !isDrifted();
    }
}
