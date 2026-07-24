using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Threading;
using Framework.Logging;
using HermesProxy.World.Enums;
using HermesProxy.World.Server;

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

    private readonly struct OutboundItem
    {
        public readonly WorldSocket Target;   // captured at enqueue so Realm/Instance routing survives a swap
        public readonly ServerPacket Packet;
        public OutboundItem(WorldSocket target, ServerPacket packet) { Target = target; Packet = packet; }
    }

    // Set true only on the writer thread so its own SendPacketReal calls (via the sink) don't re-enqueue.
    [ThreadStatic] public static bool InWriter;

    // The client-bound cast-lifecycle opcodes that MUST flow through the writer while engaged. Single
    // source of truth for the D6 chokepoint assertion (WorldSocket.SendPacketReal). Names confirmed
    // against HermesProxy/World/Enums/Opcodes.cs. Deliberately EXCLUDED: SMSG_SPELL_EXECUTE_LOG — a
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

    private readonly BlockingCollection<OutboundItem> _queue;
    private readonly IEgressSink _sink;
    private readonly object _startLock = new();
    private Thread? _thread;
    private volatile bool _started;
    private long _droppedCount;
    public volatile bool IsStopped;
    internal long DroppedCount => Interlocked.Read(ref _droppedCount);

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
    public void Enqueue(WorldSocket target, ServerPacket packet)
    {
        try
        {
            if (!_queue.TryAdd(new OutboundItem(target, packet)))
            {
                Interlocked.Increment(ref _droppedCount);
                Log.Event("honeyproxy.writer_drop", new
                {
                    opcode = packet.GetUniversalOpcode().ToString(),
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
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    _sink.Send(item.Target, item.Packet);
                }
                catch (Exception e)
                {
                    // Per-item isolation: a send failure (e.g. socket closed) must not kill the writer.
                    Log.Event("honeyproxy.writer_send_error", new
                    {
                        opcode = item.Packet.GetUniversalOpcode().ToString(),
                        error = e.Message,
                    });
                }
            }
        }
        catch (Exception e)
        {
            Log.Print(LogType.Error, $"HoneyWriter loop terminated: {e.Message}");
        }
    }

    public void Stop()
    {
        IsStopped = true;
        try { _queue.CompleteAdding(); }
        catch (Exception) { /* already completed */ }
    }
}
