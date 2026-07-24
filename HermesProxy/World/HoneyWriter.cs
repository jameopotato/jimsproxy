using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Framework.Logging;
using HermesProxy.World.Enums;
using HermesProxy.World.Server;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World;

// JimsProxy (HoneyProxy, Stage 3): the single ordered egress writer -- Pillar 2 of SUGAR-CAST-MODEL.md
// (Sugar's startSendProcessor: one channel, one drain goroutine, non-blocking drop-and-warn). Every
// client-bound packet, once the session is in-world, is enqueued here and emitted by ONE writer thread,
// so handler call order == wire order with no reorder buffer. Both client-bound entry points converge
// at WorldSocket.SendPacket (the chokepoint), so one interception covers them all.
//
// EGRESS is bounded 2048 drop-and-warn (a dropped cosmetic cast packet self-heals via the watchdog; the
// buffer is far above real cast rates so a drop is a pathological-stall valve). This is the opposite of
// the actor's LOSSLESS ingress -- an inbound packet must never be dropped, a client-bound one may be.
//
// JimsProxy (HoneyProxy, Stage 4): the writer also hosts a HOLD-UNTIL-OPCODE primitive -- Pillar 3 of
// SUGAR-CAST-MODEL.md (Sugar's SpellQueue.AddFailedPacket / GetFailedPacket "buffer-and-reconcile"). A
// fully-built ("frozen") packet can be enqueued with a release constraint ("emit only after a packet of
// opcode X [and matching spell] has been emitted"), with a bounded deadline as a fallback. The held list
// is touched ONLY on the writer thread, so no lock is needed; the DEADLINE is serviced by the writer
// thread itself via a bounded TryTake timeout while holds are pending -- the writer is the single
// sanctioned emitter and the only clock, so no Timer/Task ever produces a client-bound packet.
public sealed class HoneyWriter
{
    // Test seam (modelled on GameSessionData's callback seams): the terminal action. Production emits to
    // the socket; a FakeEgressSink records the ordered stream so tests can assert on it.
    public interface IEgressSink
    {
        void Send(WorldSocket target, ServerPacket packet);
    }

    private sealed class SocketSink : IEgressSink
    {
        public void Send(WorldSocket target, ServerPacket packet) => target.SendPacketReal(packet);
    }

    // A normal emit, a HOLD (stash until released), or a RETRACT (discard matching holds unsent). One
    // FIFO carries all three so their relative order is preserved (a retract enqueued after its hold and
    // before the release trigger always cancels the hold; the writer thread is the single arbiter).
    private enum Kind : byte { Emit, Hold, Retract }

    private readonly struct OutboundItem
    {
        public readonly Kind Kind;
        public readonly WorldSocket Target;   // captured at enqueue so Realm/Instance routing survives a swap
        public readonly ServerPacket? Packet;
        public readonly Opcode ReleaseOpcode; // Hold: emit only after this opcode is emitted
        public readonly uint ReleaseSpellId;  // Hold: 0 = any packet of ReleaseOpcode; else must match spell
        public readonly long DeadlineTicks;   // Hold: Environment.TickCount64 at/after which to release anyway
        public readonly uint RetractKey;      // Hold: retraction id (0 = not retractable); Retract: id to drop

        public OutboundItem(WorldSocket target, ServerPacket packet)
        {
            Kind = Kind.Emit; Target = target; Packet = packet;
            ReleaseOpcode = default; ReleaseSpellId = 0; DeadlineTicks = 0; RetractKey = 0;
        }

        public OutboundItem(WorldSocket target, ServerPacket packet, Opcode releaseOpcode, uint releaseSpellId, long deadlineTicks, uint retractKey)
        {
            Kind = Kind.Hold; Target = target; Packet = packet;
            ReleaseOpcode = releaseOpcode; ReleaseSpellId = releaseSpellId; DeadlineTicks = deadlineTicks; RetractKey = retractKey;
        }

        public OutboundItem(uint retractKey)
        {
            Kind = Kind.Retract; Target = null!; Packet = null;
            ReleaseOpcode = default; ReleaseSpellId = 0; DeadlineTicks = 0; RetractKey = retractKey;
        }
    }

    // A packet frozen and waiting for its release trigger or deadline. Writer-thread-local -> no lock.
    private sealed class HeldItem
    {
        public required WorldSocket Target;
        public required ServerPacket Packet;
        public required Opcode ReleaseOpcode;
        public required uint ReleaseSpellId;
        public required long DeadlineTicks;
        public required uint RetractKey;
    }

    // Set true only on the writer thread so its own SendPacketReal calls (via the sink) don't re-enqueue.
    [ThreadStatic] public static bool InWriter;

    // The client-bound cast-lifecycle opcodes that MUST flow through the writer while engaged. Single
    // source of truth for the D6 chokepoint assertion (WorldSocket.SendPacketReal). Names confirmed
    // against HermesProxy/World/Enums/Opcodes.cs. Deliberately EXCLUDED: SMSG_SPELL_EXECUTE_LOG -- a
    // combat-log narrative packet, not a kit/lifecycle driver (Fable review F4, recorded decision).
    public static readonly FrozenSet<Opcode> CastLifecycleOpcodes = new[]
    {
        Opcode.SMSG_SPELL_START, Opcode.SMSG_SPELL_GO, Opcode.SMSG_SPELL_PREPARE,
        Opcode.SMSG_CAST_FAILED, Opcode.SMSG_PET_CAST_FAILED,
        Opcode.SMSG_SPELL_FAILURE, Opcode.SMSG_SPELL_FAILED_OTHER,
        Opcode.SMSG_SPELL_COOLDOWN, Opcode.SMSG_SPELL_DELAYED,
        Opcode.SMSG_COOLDOWN_EVENT, Opcode.SMSG_CLEAR_COOLDOWN, Opcode.SMSG_COOLDOWN_CHEAT,
        Opcode.SMSG_SPELL_CHANNEL_START, Opcode.SMSG_SPELL_CHANNEL_UPDATE,
        Opcode.SMSG_CANCEL_SPELL_VISUAL, Opcode.SMSG_CANCEL_SPELL_VISUAL_KIT,
        Opcode.SMSG_PLAY_SPELL_VISUAL, Opcode.SMSG_PLAY_SPELL_VISUAL_KIT,
        Opcode.SMSG_CANCEL_AUTO_REPEAT, Opcode.SMSG_SPELL_INTERRUPT_LOG,
    }.ToFrozenSet();

    private const int DefaultCapacity = 2048; // 0x800, matching Sugar's send channel
    // Poll cadence for servicing hold deadlines while holds are pending. Small enough that a released
    // packet lands within ~one tick of its deadline, large enough to add no measurable idle CPU. This is
    // the ONLY clock in the mode -- it runs on the writer thread itself, never a Timer/Task.
    private const int HeldPollIntervalMs = 25;

    private readonly BlockingCollection<OutboundItem> _queue;
    private readonly IEgressSink _sink;
    private readonly object _startLock = new();
    private readonly List<HeldItem> _held = new(); // writer-thread-local; never touched off the writer thread
    private Thread? _thread;
    private volatile bool _started;
    private long _droppedCount;
    public volatile bool IsStopped;
    internal long DroppedCount => Interlocked.Read(ref _droppedCount);
    // Test-only visibility into the pending-hold count (writer-thread-local; read for assertions only).
    internal int HeldCountForTest => _held.Count;

    // capacity is overridable for tests only (drop-on-full is untestable at 2048).
    public HoneyWriter(IEgressSink? sink = null, int capacity = DefaultCapacity)
    {
        _sink = sink ?? new SocketSink();
        _queue = new BlockingCollection<OutboundItem>(boundedCapacity: capacity);
    }

    public void EnsureStarted()
    {
        if (_started) return;
        lock (_startLock)
        {
            if (_started) return;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "honey-writer" };
            _started = true;
            _thread.Start();
        }
    }

    // Non-blocking: on a full queue drop + warn rather than stall the producing thread (Pillar 2).
    public void Enqueue(WorldSocket target, ServerPacket packet) => TryEnqueue(new OutboundItem(target, packet), packet);

    // JimsProxy (HoneyProxy, Stage 4 -- Pillar 3): freeze `packet` and emit it ONLY after a packet of
    // `releaseOpcode` (matching `releaseSpellId`, or any when 0) is emitted; if that never happens,
    // release it anyway `deadlineMs` after enqueue (bounded fallback -- Sugar's data-dependency defer
    // with a safety valve; #379 records that an UNBOUNDED hold bricks the client cast state machine).
    // `retractKey` (non-zero) makes the hold cancellable via EnqueueRetract before it releases.
    public void EnqueueHold(WorldSocket target, ServerPacket packet, Opcode releaseOpcode, uint releaseSpellId, int deadlineMs, uint retractKey = 0)
    {
        long deadline = Environment.TickCount64 + Math.Max(0, deadlineMs);
        TryEnqueue(new OutboundItem(target, packet, releaseOpcode, releaseSpellId, deadline, retractKey), packet);
    }

    // JimsProxy (HoneyProxy, Stage 4): discard any still-pending holds tagged with `retractKey` WITHOUT
    // emitting them (a real failure crossing the #379 form-exit window retracts the unsent START). A
    // no-op if none match or they already released. Marshalled through the FIFO so it is ordered against
    // the hold it cancels and the trigger that would release it.
    public void EnqueueRetract(uint retractKey)
    {
        if (retractKey == 0) return;
        TryEnqueue(new OutboundItem(retractKey), packet: null);
    }

    private void TryEnqueue(OutboundItem item, ServerPacket? packet)
    {
        try
        {
            if (!_queue.TryAdd(item))
            {
                Interlocked.Increment(ref _droppedCount);
                Log.Event("honeyproxy.writer_drop", new
                {
                    opcode = packet?.GetUniversalOpcode().ToString() ?? "retract",
                    dropped_total = Interlocked.Read(ref _droppedCount),
                });
            }
        }
        catch (InvalidOperationException) { /* CompleteAdding'd on teardown -- drop late items */ }
    }

    private void RunLoop()
    {
        InWriter = true;
        try
        {
            while (true)
            {
                // Teardown: once no more items can arrive and the FIFO is drained, flush any holds still
                // pending (emit them so nothing is silently lost), then exit.
                if (_queue.IsAddingCompleted && _queue.Count == 0)
                {
                    FlushRemainingHolds();
                    break;
                }

                // Block indefinitely when nothing is held; poll while holds are pending so the writer
                // thread itself wakes to service deadlines (no external timer). TryTake returns false on
                // timeout OR on completed+empty; the top-of-loop check distinguishes teardown.
                int timeout = _held.Count > 0 ? HeldPollIntervalMs : Timeout.Infinite;
                if (_queue.TryTake(out var item, timeout))
                {
                    switch (item.Kind)
                    {
                        case Kind.Emit:
                            EmitAndCascade(item.Target, item.Packet!);
                            break;
                        case Kind.Hold:
                            _held.Add(new HeldItem
                            {
                                Target = item.Target,
                                Packet = item.Packet!,
                                ReleaseOpcode = item.ReleaseOpcode,
                                ReleaseSpellId = item.ReleaseSpellId,
                                DeadlineTicks = item.DeadlineTicks,
                                RetractKey = item.RetractKey,
                            });
                            break;
                        case Kind.Retract:
                            RetractHolds(item.RetractKey);
                            break;
                    }
                }

                ReleaseExpiredHolds();
            }
        }
        catch (Exception e)
        {
            Log.Print(LogType.Error, $"HoneyWriter loop terminated: {e.Message}");
        }
    }

    // Emit one packet to the sink, then release (in FIFO order) any holds triggered by it -- and by any
    // hold that release cascades into (e.g. a form-removal UPDATE_OBJECT releasing a held SPELL_START,
    // whose emit in turn releases a held SPELL_GO). Allocation-free in the common case (no holds).
    private void EmitAndCascade(WorldSocket target, ServerPacket packet)
    {
        Emit(target, packet);
        if (_held.Count == 0)
            return;

        var work = new Queue<(WorldSocket Target, ServerPacket Packet)>();
        work.Enqueue((target, packet));
        while (work.Count > 0 && _held.Count > 0)
        {
            var (_, trigger) = work.Dequeue();
            Opcode op = trigger.GetUniversalOpcode();
            uint spellId = ExtractSpellId(trigger);
            for (int i = 0; i < _held.Count; )
            {
                var h = _held[i];
                if (h.ReleaseOpcode == op && (h.ReleaseSpellId == 0 || h.ReleaseSpellId == spellId))
                {
                    _held.RemoveAt(i);
                    Emit(h.Target, h.Packet);
                    work.Enqueue((h.Target, h.Packet));
                }
                else
                {
                    i++;
                }
            }
        }
    }

    // Release holds whose deadline has passed (their trigger never came -- e.g. a real cast failure that
    // gets no SPELL_GO). Each release cascades exactly like a trigger emit.
    private void ReleaseExpiredHolds()
    {
        if (_held.Count == 0)
            return;
        long now = Environment.TickCount64;
        for (int i = 0; i < _held.Count; )
        {
            var h = _held[i];
            if (now >= h.DeadlineTicks)
            {
                _held.RemoveAt(i);
                EmitAndCascade(h.Target, h.Packet); // may mutate _held
                i = 0; // restart: the cascade may have removed/reordered entries
            }
            else
            {
                i++;
            }
        }
    }

    private void RetractHolds(uint retractKey)
    {
        if (retractKey == 0 || _held.Count == 0)
            return;
        int removed = 0;
        for (int i = 0; i < _held.Count; )
        {
            if (_held[i].RetractKey == retractKey)
            {
                _held.RemoveAt(i);
                removed++;
            }
            else
            {
                i++;
            }
        }
        if (removed > 0 && Framework.Settings.DebugOutput)
            Log.Event("honeyproxy.hold_retracted", new { retract_key = retractKey, count = removed });
    }

    // Teardown: emit every still-pending hold in FIFO order so no frozen packet is silently lost.
    private void FlushRemainingHolds()
    {
        if (_held.Count == 0)
            return;
        var pending = _held.ToArray();
        _held.Clear();
        foreach (var h in pending)
            Emit(h.Target, h.Packet);
    }

    private void Emit(WorldSocket target, ServerPacket packet)
    {
        try
        {
            _sink.Send(target, packet);
        }
        catch (Exception e)
        {
            // Per-item isolation: a send failure (e.g. socket closed) must not kill the writer.
            Log.Event("honeyproxy.writer_send_error", new
            {
                opcode = packet.GetUniversalOpcode().ToString(),
                error = e.Message,
            });
        }
    }

    // Only SMSG_SPELL_GO carries a spell id the hold primitive keys on today (the CAST_FAILED reconcile);
    // every other release trigger keys on opcode alone (ReleaseSpellId == 0). Kept deliberately narrow so
    // the writer stays decoupled from packet internals.
    private static uint ExtractSpellId(ServerPacket packet)
        => packet is SpellGo go ? (uint)go.Cast.SpellID : 0u;

    public void Stop()
    {
        IsStopped = true;
        try { _queue.CompleteAdding(); }
        catch (Exception) { /* already completed */ }
    }
}
