using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Manual override commands with Undo / Redo.
//
//   CommandUndoStack  Stack<CommandRecord> (SmartXDataStructures)  - every executed command is pushed here. Undo
//                                  pops the most recent one (LIFO) and writes
//                                  its PreviousStates back to the devices.
//   CommandRedoStack  Stack<CommandRecord>  - undone commands land here so they can be
//                                  re-applied. Issuing a brand-new command
//                                  clears it (same rule as any text editor).
//
// Because each record stores the before/after state of EVERY device it
// touched, a single Undo reverts a whole "shut down all valves" action.
public class CommandService(SmartXDataStructures ds, DeviceStateStore states, ActionEngine engine, LiveBroadcaster live)
{
    private const int MaxHistory = 100;

    private readonly object _lock = new();
    private Stack<CommandRecord> _undo => ds.CommandUndoStack;
    private Stack<CommandRecord> _redo => ds.CommandRedoStack;
    private readonly LinkedList<CommandLogEntry> _log = new();
    private int _nextId = 1;

    public CommandResult Execute(CommandRequest request)
    {
        var issuedBy = string.IsNullOrWhiteSpace(request.IssuedBy) ? "Operator" : request.IssuedBy!;
        var previous = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string label;

        switch (request.Type)
        {
            case CommandType.ShutDownAllValves:
            case CommandType.OpenAllValves:
            {
                var open = request.Type == CommandType.OpenAllValves;
                var valves = states.Valves();
                if (valves.Count == 0) return new CommandResult(false, "No valves are registered.", null);

                foreach (var (mac, on) in valves)
                {
                    previous[mac] = on;
                    next[mac] = open;
                }

                label = open ? $"Open all valves ({valves.Count})" : $"Shut down all valves ({valves.Count})";
                break;
            }

            default:
            {
                if (string.IsNullOrWhiteSpace(request.TargetMac))
                    return new CommandResult(false, "targetMac is required for this command.", null);

                if (!states.TryGetActuator(request.TargetMac, out var isOn, out var name))
                    return new CommandResult(false, $"{request.TargetMac} is not a registered actuator.", null);

                var target = request.Type switch
                {
                    CommandType.TurnOn => true,
                    CommandType.TurnOff => false,
                    _ => !isOn,
                };

                previous[request.TargetMac] = isOn;
                next[request.TargetMac] = target;
                label = $"{(target ? "Turn ON" : "Turn OFF")} {name}";
                break;
            }
        }

        CommandRecord record;
        lock (_lock)
        {
            record = new CommandRecord
            {
                Id = _nextId++,
                Type = request.Type,
                TargetMac = request.TargetMac,
                Label = label,
                IssuedBy = issuedBy,
                PreviousStates = previous,
                NewStates = next,
            };

            Apply(record.NewStates);
            _undo.Push(record);
            _redo.Clear();
            TrimStack(_undo);
            Log(record, "Executed", issuedBy);
        }

        // Only human commands teach the pattern model - otherwise the auto
        // engine would end up "learning" from its own decisions.
        if (issuedBy != "Auto-Engine")
        {
            engine.Record(new ActivityEvent(ActivityKind.Command, CommandKey(request), label, DateTime.UtcNow));
        }

        live.Publish("command", new { action = "executed", record.Id, record.Label, record.IssuedBy });
        return new CommandResult(true, $"{label} sent.", record);
    }

    public CommandResult Undo()
    {
        lock (_lock)
        {
            if (!_undo.TryPop(out var record)) return new CommandResult(false, "Nothing to undo.", null);

            Apply(record.PreviousStates);
            _redo.Push(record);
            Log(record, "Undone", "Operator");
            live.Publish("command", new { action = "undone", record.Id, record.Label });
            return new CommandResult(true, $"Reverted: {record.Label}", record);
        }
    }

    public CommandResult Redo()
    {
        lock (_lock)
        {
            if (!_redo.TryPop(out var record)) return new CommandResult(false, "Nothing to redo.", null);

            Apply(record.NewStates);
            _undo.Push(record);
            Log(record, "Redone", "Operator");
            live.Publish("command", new { action = "redone", record.Id, record.Label });
            return new CommandResult(true, $"Re-applied: {record.Label}", record);
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                // Stack<T> enumerates top-first, so [0] is what Undo will revert next.
                undoStack = _undo.Take(15).Select(Summarise).ToList(),
                redoStack = _redo.Take(15).Select(Summarise).ToList(),
                undoDepth = _undo.Count,
                redoDepth = _redo.Count,
                log = _log.Take(30).ToList(),
            };
        }
    }

    public static string CommandKey(CommandRequest r) => r.Type switch
    {
        CommandType.ShutDownAllValves or CommandType.OpenAllValves => $"cmd|{r.Type}|*",
        _ => $"cmd|{r.Type}|{r.TargetMac}",
    };

    private void Apply(Dictionary<string, bool> states_)
    {
        foreach (var (mac, on) in states_) states.SetActuator(mac, on);
    }

    private void Log(CommandRecord record, string action, string by)
    {
        _log.AddFirst(new CommandLogEntry(record.Id, action, record.Label, by, DateTime.UtcNow));
        while (_log.Count > MaxHistory) _log.RemoveLast();
    }

    // Stack<T> has no "remove from bottom", so rebuild it when it gets too deep.
    private static void TrimStack(Stack<CommandRecord> stack)
    {
        if (stack.Count <= MaxHistory) return;
        var keep = stack.Take(MaxHistory).Reverse().ToList();
        stack.Clear();
        foreach (var r in keep) stack.Push(r);
    }

    private static object Summarise(CommandRecord r) => new
    {
        r.Id,
        r.Label,
        type = r.Type.ToString(),
        r.IssuedBy,
        r.IssuedAt,
        devicesAffected = r.NewStates.Count,
    };
}
