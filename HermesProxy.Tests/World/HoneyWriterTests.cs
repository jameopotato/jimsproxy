using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// HoneyProxy Stage-3 egress-writer semantics (pulled forward from Stage 6 per Fable review F6):
//   1. every enqueued packet reaches the sink on ONE writer thread, with each producer's
//      subsequence in enqueue order (the FIFO property the mode exists to guarantee);
//   2. a full queue drops-and-warns (never blocks the producer) and the writer keeps draining;
//   3. Stop() is safe: no throw on late enqueues, previously queued items still drain.
// Uses the injectable IEgressSink seam; the WorldSocket target is never dereferenced by the
// writer itself, so tests pass null.
public class HoneyWriterTests
{
    static HoneyWriterTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    // Minimal concrete ServerPacket carrying a producer/sequence tag.
    private sealed class TaggedPacket : ServerPacket
    {
        public readonly int Producer;
        public readonly int Sequence;
        public TaggedPacket(int producer, int sequence) : base(Opcode.SMSG_SPELL_GO)
        {
            Producer = producer;
            Sequence = sequence;
        }
        public override void Write() { }
    }

    private sealed class RecordingSink : HoneyWriter.IEgressSink
    {
        public readonly ConcurrentQueue<(int ThreadId, TaggedPacket Packet)> Received = new();
        public readonly CountdownEvent Countdown;
        // Optional gate: when set, the FIRST Send blocks until released (drop-on-full test).
        public ManualResetEventSlim? GateFirstSend;
        public volatile bool FirstSendEntered;
        private int _gated;

        public RecordingSink(int expected) => Countdown = new CountdownEvent(expected);

        public void Send(HermesProxy.World.Server.WorldSocket target, ServerPacket packet)
        {
            if (GateFirstSend != null && Interlocked.Exchange(ref _gated, 1) == 0)
            {
                FirstSendEntered = true;
                GateFirstSend.Wait(TimeSpan.FromSeconds(10));
            }
            Received.Enqueue((Environment.CurrentManagedThreadId, (TaggedPacket)packet));
            if (!Countdown.IsSet)
                Countdown.Signal();
        }
    }

    [Fact]
    public void MultiProducer_AllDelivered_SingleThread_PerProducerFifo()
    {
        const int producers = 4, perProducer = 50;
        var sink = new RecordingSink(producers * perProducer);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        var threads = Enumerable.Range(0, producers).Select(p => new Thread(() =>
        {
            for (int i = 0; i < perProducer; i++)
                writer.Enqueue(null!, new TaggedPacket(p, i));
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.True(sink.Countdown.Wait(TimeSpan.FromSeconds(10)), "writer did not drain all packets");

        var received = sink.Received.ToList();
        Assert.Equal(producers * perProducer, received.Count);
        // One drain thread only.
        Assert.Single(received.Select(r => r.ThreadId).Distinct());
        // Each producer's packets arrive in its enqueue order.
        for (int p = 0; p < producers; p++)
        {
            var seq = received.Where(r => r.Packet.Producer == p).Select(r => r.Packet.Sequence).ToList();
            Assert.Equal(Enumerable.Range(0, perProducer).ToList(), seq);
        }

        writer.Stop();
    }

    [Fact]
    public void FullQueue_DropsAndWarns_NeverBlocks_KeepsDraining()
    {
        var gate = new ManualResetEventSlim(false);
        var sink = new RecordingSink(expected: 3) { GateFirstSend = gate };
        var writer = new HoneyWriter(sink, capacity: 2);
        writer.EnsureStarted();

        // First packet occupies the (gated) sink; the queue behind it holds 2; the rest must drop.
        writer.Enqueue(null!, new TaggedPacket(0, 0));
        // Wait until the writer has TAKEN item 0 into the gated Send, so the queue's full capacity
        // (2) is free for the next enqueues and the drop boundary is deterministic.
        Assert.True(SpinWait.SpinUntil(() => sink.FirstSendEntered, TimeSpan.FromSeconds(5)),
            "writer never took the first item");
        writer.Enqueue(null!, new TaggedPacket(0, 1));
        writer.Enqueue(null!, new TaggedPacket(0, 2));
        writer.Enqueue(null!, new TaggedPacket(0, 3)); // beyond capacity → dropped
        writer.Enqueue(null!, new TaggedPacket(0, 4)); // beyond capacity → dropped

        Assert.True(writer.DroppedCount >= 1, "expected at least one drop on a full queue");

        gate.Set(); // release the sink; writer must keep draining what survived
        Assert.True(sink.Countdown.Wait(TimeSpan.FromSeconds(10)), "writer did not resume draining after the stall");
        Assert.True(sink.Received.Count >= 3);

        writer.Stop();
    }

    [Fact]
    public void Stop_IsSafe_DrainsQueued_SwallowsLateEnqueues()
    {
        var sink = new RecordingSink(expected: 2);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        writer.Enqueue(null!, new TaggedPacket(0, 0));
        writer.Enqueue(null!, new TaggedPacket(0, 1));
        writer.Stop();

        // Late enqueue after Stop must not throw (and is dropped by design at teardown).
        var ex = Record.Exception(() => writer.Enqueue(null!, new TaggedPacket(0, 2)));
        Assert.Null(ex);

        // Items enqueued before Stop still drain (GetConsumingEnumerable semantics).
        Assert.True(sink.Countdown.Wait(TimeSpan.FromSeconds(10)), "pre-Stop items did not drain");
    }

    // ---- HoneyProxy Stage-4 hold-until-opcode primitive (Pillar 3 buffer-and-reconcile + #379 ordering) ----

    // A ServerPacket with an arbitrary opcode (base opcode drives GetUniversalOpcode()); no wire body.
    private sealed class OpcodePacket : ServerPacket
    {
        public OpcodePacket(Opcode opcode) : base(opcode) { }
        public override void Write() { }
    }

    // Records the exact emit order (single writer thread) so tests can assert on the wire sequence.
    private sealed class OrderSink : HoneyWriter.IEgressSink
    {
        public readonly ConcurrentQueue<ServerPacket> Order = new();
        private readonly CountdownEvent _countdown;
        public OrderSink(int expected) => _countdown = new CountdownEvent(expected);
        public void Send(HermesProxy.World.Server.WorldSocket target, ServerPacket packet)
        {
            Order.Enqueue(packet);
            if (!_countdown.IsSet) _countdown.Signal();
        }
        public bool Wait(TimeSpan t) => _countdown.Wait(t);
        public System.Collections.Generic.List<Opcode> Opcodes()
            => Order.Select(p => p.GetUniversalOpcode()).ToList();
    }

    // (a) A CAST_FAILED held behind SPELL_GO for an in-flight cast is replayed strictly AFTER the GO:
    //     enqueue START, hold FAILED behind GO, enqueue GO -> wire order START, GO, FAILED.
    [Fact]
    public void Hold_FailedBehindGo_ReplaysAfterGo()
    {
        var sink = new OrderSink(expected: 3);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        writer.Enqueue(null!, new OpcodePacket(Opcode.SMSG_SPELL_START));
        writer.EnqueueHold(null!, new OpcodePacket(Opcode.SMSG_CAST_FAILED),
            releaseOpcode: Opcode.SMSG_SPELL_GO, releaseSpellId: 0, deadlineMs: 60_000);
        writer.Enqueue(null!, new OpcodePacket(Opcode.SMSG_SPELL_GO));

        Assert.True(sink.Wait(TimeSpan.FromSeconds(10)), "writer did not emit all three");
        Assert.Equal(
            new[] { Opcode.SMSG_SPELL_START, Opcode.SMSG_SPELL_GO, Opcode.SMSG_CAST_FAILED },
            sink.Opcodes());

        writer.Stop();
    }

    // (b) An unheld packet flows through unchanged (same reference, no reordering).
    [Fact]
    public void Emit_UnheldPacket_FlowsThroughUnchanged()
    {
        var sink = new OrderSink(expected: 1);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        var pkt = new OpcodePacket(Opcode.SMSG_SPELL_GO);
        writer.Enqueue(null!, pkt);

        Assert.True(sink.Wait(TimeSpan.FromSeconds(5)));
        Assert.Same(pkt, Assert.Single(sink.Order));

        writer.Stop();
    }

    // (c) A hold whose trigger never comes is released by its deadline (the writer thread's TryTake
    //     timeout is the clock -- no Timer/Task). No SPELL_GO is ever emitted here.
    [Fact]
    public void Hold_DeadlineExpires_ReleasedWithoutTrigger()
    {
        var sink = new OrderSink(expected: 1);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        var failed = new OpcodePacket(Opcode.SMSG_CAST_FAILED);
        writer.EnqueueHold(null!, failed,
            releaseOpcode: Opcode.SMSG_SPELL_GO, releaseSpellId: 0, deadlineMs: 50);

        Assert.True(sink.Wait(TimeSpan.FromSeconds(5)), "deadline did not release the held packet");
        Assert.Same(failed, Assert.Single(sink.Order));

        writer.Stop();
    }

    // (d) Releases cascade: a form-removal UPDATE_OBJECT releases a held SPELL_GO, whose emit in turn
    //     releases a CAST_FAILED held behind SPELL_GO -- and the GO match is keyed on the real spell id
    //     (exercises ExtractSpellId over a genuine SpellGo). Wire order: UPDATE_OBJECT, GO, FAILED.
    [Fact]
    public void Hold_Cascade_UpdateObjectReleasesGo_GoReleasesFailedBySpellId()
    {
        var sink = new OrderSink(expected: 3);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        var go = new SpellGo();
        go.Cast.SpellID = 1234;
        writer.EnqueueHold(null!, go,
            releaseOpcode: Opcode.SMSG_UPDATE_OBJECT, releaseSpellId: 0, deadlineMs: 60_000);
        writer.EnqueueHold(null!, new OpcodePacket(Opcode.SMSG_CAST_FAILED),
            releaseOpcode: Opcode.SMSG_SPELL_GO, releaseSpellId: 1234, deadlineMs: 60_000);
        writer.Enqueue(null!, new OpcodePacket(Opcode.SMSG_UPDATE_OBJECT));

        Assert.True(sink.Wait(TimeSpan.FromSeconds(10)), "cascade did not release both holds");
        Assert.Equal(
            new[] { Opcode.SMSG_UPDATE_OBJECT, Opcode.SMSG_SPELL_GO, Opcode.SMSG_CAST_FAILED },
            sink.Opcodes());

        writer.Stop();
    }

    // (d2) A hold keyed on a specific spell id is NOT released by a GO for a DIFFERENT spell; the
    //      deadline is the only thing that frees it.
    [Fact]
    public void Hold_SpellIdMismatch_NotReleasedByOtherSpellGo()
    {
        var sink = new OrderSink(expected: 2);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        writer.EnqueueHold(null!, new OpcodePacket(Opcode.SMSG_CAST_FAILED),
            releaseOpcode: Opcode.SMSG_SPELL_GO, releaseSpellId: 999, deadlineMs: 300);
        var otherGo = new SpellGo();
        otherGo.Cast.SpellID = 111; // different spell -> must NOT release the hold
        writer.Enqueue(null!, otherGo);

        // The mismatched GO emits immediately; the FAILED only after its 300ms deadline.
        Assert.True(sink.Wait(TimeSpan.FromSeconds(5)));
        var order = sink.Opcodes();
        Assert.Equal(Opcode.SMSG_SPELL_GO, order[0]);
        Assert.Equal(Opcode.SMSG_CAST_FAILED, order[1]);

        writer.Stop();
    }

    // (e) A retracted hold is discarded unsent: hold START behind UPDATE_OBJECT, retract it, then emit
    //     UPDATE_OBJECT -> only the UPDATE_OBJECT reaches the sink.
    [Fact]
    public void Hold_Retracted_DiscardedUnsent()
    {
        var sink = new OrderSink(expected: 1);
        var writer = new HoneyWriter(sink);
        writer.EnsureStarted();

        writer.EnqueueHold(null!, new OpcodePacket(Opcode.SMSG_SPELL_START),
            releaseOpcode: Opcode.SMSG_UPDATE_OBJECT, releaseSpellId: 0, deadlineMs: 60_000, retractKey: 7);
        writer.EnqueueRetract(7);
        writer.Enqueue(null!, new OpcodePacket(Opcode.SMSG_UPDATE_OBJECT));

        Assert.True(sink.Wait(TimeSpan.FromSeconds(5)));
        // Give the writer a beat to (wrongly) emit the retracted START, then assert it never did.
        Thread.Sleep(150);
        Assert.Equal(Opcode.SMSG_UPDATE_OBJECT, Assert.Single(sink.Order).GetUniversalOpcode());

        writer.Stop();
    }
}
