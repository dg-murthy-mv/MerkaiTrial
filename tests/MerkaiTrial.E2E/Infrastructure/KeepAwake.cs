// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/KeepAwake.cs
//
// NEW (095). Keeps Windows from going to sleep, or turning the screen off
// and suspending the network, while the tests run.
//
// WHY
//   A run on 18 Oct failed one test with Chromium's own error
//
//       net::ERR_NETWORK_IO_SUSPENDED
//
//   which is what the browser reports when WINDOWS suspends network
//   activity, as it does when the computer starts to sleep, or on many
//   laptops when the screen turns off ("modern standby"). The same run has
//   a gap of several minutes in both apps' logs, and the steps around it
//   failed in ways that make no sense for a working app: pages that had
//   loaded but never "finished", a password page that never moved on. A
//   full run takes 10–15 minutes, long enough for the power settings to
//   kick in when nobody touches the keyboard.
//
// HOW
//   SetThreadExecutionState, the call video players and installers use:
//   "the system and the display are needed until I say otherwise". It is
//   held by one dedicated thread for the whole run and released at the
//   end (or when the process exits, whichever comes first). It changes no
//   setting: the computer's own power plan applies again the moment the
//   run is over.
//
//   On anything but Windows it does nothing.
// =====================================================================

using System.Runtime.InteropServices;

namespace MerkaiTrial.E2E.Infrastructure;

public static class KeepAwake
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired  = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous      = 0x80000000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    private static Thread? _holder;
    private static ManualResetEventSlim? _release;

    /// <summary>Holds the computer awake until <see cref="Stop"/>. Safe to call more than once.</summary>
    public static void Start()
    {
        if (!OperatingSystem.IsWindows() || _holder is not null) return;

        var release = _release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var ok = false;

        // The request belongs to the thread that makes it, so one thread
        // makes it and stays alive, asleep, until the run ends.
        _holder = new Thread(() =>
        {
            try
            {
                const ExecutionState awake =
                    ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired;

                // The call returns the PREVIOUS state, which can itself be
                // 0 on a new thread, so success is checked by asking again:
                // the second call must report exactly what the first set.
                SetThreadExecutionState(awake);
                ok = SetThreadExecutionState(awake) == awake;
            }
            catch
            {
                ok = false;
            }
            started.Set();

            release.Wait();

            try { SetThreadExecutionState(ExecutionState.Continuous); } catch { /* exiting anyway */ }
        })
        {
            IsBackground = true,      // never keeps the test process alive
            Name = "e2e-keep-awake",
        };
        _holder.Start();
        started.Wait(TimeSpan.FromSeconds(5));

        TestContext.Progress.WriteLine(ok
            ? "[e2e setup] Keeping the computer and screen awake during the run."
            : "[e2e setup] Could not ask Windows to stay awake — if the computer sleeps during the run, tests will fail.");
    }

    /// <summary>Lets the computer's own power settings apply again.</summary>
    public static void Stop()
    {
        if (_holder is null || _release is null) return;
        _release.Set();
        _holder.Join(TimeSpan.FromSeconds(5));
        _holder = null;
        _release = null;
    }
}
