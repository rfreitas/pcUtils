namespace ClaudeFleet;

/// <summary>
/// Decides whether the 5 s background sample timer should run (plan "Cost model"): it runs while any session is busy or
/// Claude still uses CPU (a background build outlives its session's turn), and stops after
/// <see cref="QuietSamples"/> consecutive quiet samples. Once stopped, only the registry watcher and the exit waits
/// can wake the app, and they call <see cref="Observe"/> again.
/// </summary>
internal sealed class SamplingGovernor
{
    public const int QuietSamples = 2;
    /// <summary>Aggregate CPU (% of the machine) at or above which Claude counts as still working.</summary>
    public const double ActiveCpu = 1.0;

    private int _quiet;

    public bool Running { get; private set; }

    /// <summary>Feeds one sample; returns whether the timer should now be running.</summary>
    public bool Observe(FleetSnapshot snapshot)
    {
        if (snapshot.Busy > 0 || snapshot.TotalCpu >= ActiveCpu)
        {
            _quiet = 0;
            Running = true;
        }
        else if (Running && ++_quiet >= QuietSamples)
        {
            Running = false;
        }
        return Running;
    }
}
