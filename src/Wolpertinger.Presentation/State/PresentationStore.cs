using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.State;

public sealed record PresentationStoreState(
    PresentationConnectionState Connection,
    PresentationSnapshot? Snapshot);

public sealed class PresentationStore
{
    public PresentationStoreState State { get; private set; } =
        new(PresentationConnectionState.Connecting, null);

    public event EventHandler? StateChanged;

    public bool ApplySnapshot(PresentationSnapshot snapshot)
    {
        try
        {
            PresentationSnapshotValidator.Validate(snapshot);
        }
        catch (InvalidDataException)
        {
            MarkIncompatible();
            throw;
        }

        var current = State.Snapshot;
        if (current is not null)
        {
            if (snapshot.Revision < current.Revision)
                return false;

            if (snapshot.Revision == current.Revision)
            {
                if (snapshot != current)
                {
                    MarkIncompatible();
                    throw new InvalidDataException("Conflicting presentation snapshot at the current revision.");
                }

                if (State.Connection == PresentationConnectionState.Live)
                    return false;

                SetState(new(PresentationConnectionState.Live, current));
                return true;
            }
        }

        SetState(new(PresentationConnectionState.Live, snapshot));
        return true;
    }

    public void MarkConnecting() => SetConnection(PresentationConnectionState.Connecting);

    public void MarkDisconnected() => SetConnection(PresentationConnectionState.Disconnected);

    public void MarkIncompatible() => SetConnection(PresentationConnectionState.Incompatible);

    private void SetConnection(PresentationConnectionState connection)
    {
        if (State.Connection == connection)
            return;

        SetState(State with { Connection = connection });
    }

    private void SetState(PresentationStoreState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
