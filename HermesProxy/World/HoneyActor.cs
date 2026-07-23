using System;
using System.Collections.Concurrent;
using System.Threading;
using Framework.Logging;
using HermesProxy.World.Client;
using HermesProxy.World.Server;

namespace HermesProxy.World;

// JimsProxy (HoneyProxy, Stage 2): the single actor — Pillar 1 of SUGAR-CAST-MODEL.md (Sugar's
// PacketWorker). When HoneyProxyMode is engaged, ALL in-world (post-login) packet dispatch, both
// directions, runs on this one thread in arrival (FIFO) order, instead of concurrently on the
// WorldClient ReceiveLoop + the two WorldSocket IOCP threads. Two casts can therefore never be
// processed concurrently, so a cross-cast visual-kit collision is structurally impossible.
//
// Handlers run UNCHANGED via the existing dispatch tables (WorldClient.HandlePacketFromActor /
// WorldSocket.HandlePacket); only the thread they run on changes. Ingress is LOSSLESS (unbounded) —
// dropping an inbound SMSG_UPDATE_OBJECT would desync the world; the 2048 drop-and-warn policy is the
// EGRESS writer (HoneyWriter, Stage 3), not here.
public sealed class HoneyActor
{
    public enum Direction { Legacy, Modern }

    private readonly struct WorkItem
    {
        public readonly Direction Dir;
        public readonly WorldPacket? Packet;
        public readonly WorldSocket? ModernSender;
        public readonly Action? Continuation; // Stage 5: timers/tasks marshal their emit onto the actor

        public WorkItem(Direction dir, WorldPacket packet, WorldSocket? sender)
        {
            Dir = dir; Packet = packet; ModernSender = sender; Continuation = null;
        }

        public WorkItem(Action continuation)
        {
            Dir = default; Packet = null; ModernSender = null; Continuation = continuation;
        }
    }

    private readonly GlobalSessionData _session;
    private readonly BlockingCollection<WorkItem> _queue = new(); // unbounded => lossless ingress
    private readonly object _startLock = new();
    private Thread? _thread;
    private volatile bool _started;
    public volatile bool IsStopped;

    public HoneyActor(GlobalSessionData session) => _session = session;

    // Idempotent; called at the in-world handoff (WorldSocket.HandleEnterEncryptedModeAck).
    public void EnsureStarted()
    {
        if (_started) return;
        lock (_startLock)
        {
            if (_started) return;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "honey-actor" };
            _started = true;
            _thread.Start();
        }
    }

    public void EnqueueLegacy(WorldPacket packet)
        => Enqueue(new WorkItem(Direction.Legacy, Copy(packet), null));

    public void EnqueueModern(WorldSocket sender, WorldPacket packet)
        => Enqueue(new WorkItem(Direction.Modern, Copy(packet), sender));

    private void Enqueue(WorkItem item)
    {
        try { _queue.Add(item); }
        catch (InvalidOperationException) { /* CompleteAdding'd on teardown — drop late items */ }
    }

    // Defensive copy at enqueue (correctness pin): the modern receive path aliases
    // WorldSocket._packetBuffer and Reset()s it immediately (ReadData:258-259), so the backing array is
    // reused for the next packet. Copying here decouples the actor from the receive layer's buffer
    // strategy entirely. GetData() in read mode returns the full backing array (opcode prefix +
    // payload); re-wrapping via new WorldPacket(bytes) reproduces the exact stored opcode + position-2
    // read state. Applied uniformly (the legacy buffer is already fresh) so a future buffer-pool change
    // can't reintroduce a use-after-overwrite race.
    private static WorldPacket Copy(WorldPacket p)
    {
        var copy = new WorldPacket((byte[])p.GetData().Clone());
        copy.SetReceiveTime(p.GetReceivedTime());
        return copy;
    }

    private void RunLoop()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (item.Continuation != null)
                    {
                        item.Continuation();
                    }
                    else if (item.Dir == Direction.Legacy)
                    {
                        _session.WorldClient?.HandlePacketFromActor(item.Packet!);
                    }
                    else
                    {
                        item.ModernSender?.HandlePacket(item.Packet!);
                    }
                }
                catch (Exception e)
                {
                    // Per-item isolation: a throwing handler must not kill the actor (the receive loops
                    // already isolate per-packet; mirror that here).
                    Log.Event("honeyproxy.actor_dispatch_error", new
                    {
                        kind = item.Continuation != null ? "post" : item.Dir.ToString(),
                        error = e.Message,
                    });
                }
            }
        }
        catch (Exception e)
        {
            Log.Print(LogType.Error, $"HoneyActor loop terminated: {e.Message}");
        }
    }

    // Final session teardown (GlobalSessionData.OnDisconnect): stop accepting; the loop drains + exits.
    public void Stop()
    {
        IsStopped = true;
        try { _queue.CompleteAdding(); }
        catch (Exception) { /* already completed */ }
    }
}
