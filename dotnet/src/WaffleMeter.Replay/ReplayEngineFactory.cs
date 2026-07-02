namespace WaffleMeter.Replay;

/// <summary>
/// The single public entry point of the private engine assembly: <see cref="ReplayEngineLoader"/> finds
/// this by reflection (it implements <see cref="IReplayEngineFactory"/> with a parameterless ctor) and
/// uses it to build a <see cref="MovementCaptureService"/> without any compile-time reference from the
/// app. Keep it as the ONLY public <see cref="IReplayEngineFactory"/> in this assembly.
/// </summary>
public sealed class ReplayEngineFactory : IReplayEngineFactory
{
    public IReplayEngine Create(IReplayIdentitySource? extraIdentity, string? persistDir)
        => new MovementCaptureService(extraIdentity, persistDir);
}
