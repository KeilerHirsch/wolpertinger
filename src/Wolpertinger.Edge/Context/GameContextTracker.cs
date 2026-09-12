using System.Text.Json;

namespace Wolpertinger.Edge.Context;

public sealed class GameContextTracker
{
    private GameContext _baseContext = GameContext.InactiveOrNoGame;
    private GameContext? _focusContext;
    private bool _sessionActive;

    public GameContext Current => _focusContext ?? _baseContext;

    public GameContextTransition ApplyLifecycle(GameContext context)
    {
        _baseContext = context;
        _focusContext = null;
        _sessionActive = context != GameContext.InactiveOrNoGame;
        return Transition(GameContextSignalOrigin.Lifecycle);
    }

    public GameContextTransition ApplyJournal(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return ApplyJournal(document.RootElement);
        }
        catch (JsonException)
        {
            return SetUnknown(GameContextSignalOrigin.Journal);
        }
    }

    public GameContextTransition ApplyStatus(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!TryGetUInt64(root, "Flags", out var flags)
                || !TryGetInt32(root, "GuiFocus", out var guiFocus))
            {
                return SetUnknown(GameContextSignalOrigin.StatusSnapshot);
            }

            var docked = (flags & 1UL) != 0;
            var supercruise = (flags & 16UL) != 0;
            if (docked && supercruise)
            {
                return SetUnknown(GameContextSignalOrigin.StatusSnapshot);
            }

            if (docked) _baseContext = GameContext.Docked;
            else if (supercruise) _baseContext = GameContext.Supercruise;
            else if (_sessionActive) _baseContext = GameContext.Flight;
            else _baseContext = GameContext.InactiveOrNoGame;

            _focusContext = guiFocus switch
            {
                5 => GameContext.StationServices,
                6 => GameContext.GalaxyMap,
                7 => GameContext.SystemMap,
                _ => null,
            };
            return Transition(GameContextSignalOrigin.StatusSnapshot);
        }
        catch (JsonException)
        {
            return SetUnknown(GameContextSignalOrigin.StatusSnapshot);
        }
    }

    public GameContextTransition ApplyTrustedFsdJump()
    {
        _sessionActive = true;
        _baseContext = GameContext.PostJump;
        _focusContext = null;
        return Transition(GameContextSignalOrigin.Trusted);
    }

    private GameContextTransition ApplyJournal(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventValue)
            || eventValue.ValueKind != JsonValueKind.String)
        {
            return SetUnknown(GameContextSignalOrigin.Journal);
        }

        var eventName = eventValue.GetString();
        switch (eventName)
        {
            case "Fileheader":
                _sessionActive = true;
                if (_baseContext is GameContext.InactiveOrNoGame or GameContext.MainMenu)
                    _baseContext = GameContext.MainMenu;
                _focusContext = null;
                break;
            case "Docked":
                SetBase(GameContext.Docked);
                break;
            case "Undocked":
                SetBase(GameContext.Flight);
                break;
            case "FSDTarget":
                SetBase(GameContext.JumpPreparation);
                break;
            case "StartJump":
                return ApplyStartJump(root);
            case "Shutdown":
                _sessionActive = false;
                _baseContext = GameContext.InactiveOrNoGame;
                _focusContext = null;
                break;
        }
        return Transition(GameContextSignalOrigin.Journal);
    }

    private GameContextTransition ApplyStartJump(JsonElement root)
    {
        if (!root.TryGetProperty("JumpType", out var jumpType)
            || jumpType.ValueKind != JsonValueKind.String)
        {
            return SetUnknown(GameContextSignalOrigin.Journal);
        }

        if (string.Equals(jumpType.GetString(), "Hyperspace", StringComparison.Ordinal))
        {
            SetBase(GameContext.FsdJump);
        }
        return Transition(GameContextSignalOrigin.Journal);
    }

    private void SetBase(GameContext context)
    {
        _sessionActive = true;
        _baseContext = context;
        _focusContext = null;
    }

    private GameContextTransition SetUnknown(GameContextSignalOrigin origin)
    {
        _baseContext = GameContext.Unknown;
        _focusContext = null;
        return Transition(origin);
    }

    private GameContextTransition Transition(GameContextSignalOrigin origin)
        => new(Current, origin);

    private static bool TryGetUInt64(JsonElement root, string name, out ulong value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetUInt64(out value);
    }

    private static bool TryGetInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}
