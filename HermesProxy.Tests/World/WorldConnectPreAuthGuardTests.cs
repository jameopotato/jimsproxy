using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (#388 "World Server Down" on a fast character switch). A world connect's outcome
/// flag is decided by whatever writes it first while it is still undecided. On origin/beta the
/// send path's catch wrote it, so ANY send that landed while the legacy TCP connect was still
/// pending decided the connect as failed, ConnectToWorldServer returned false and the login was
/// aborted with NoWorld. The sender in every field failure was the previous character's
/// quest-query drainer, which captures its GameState once and nothing stops at logout.
///
/// Two fixes, each pinned here:
///  - WorldClient contract: before the handshake completes only CMSG_AUTH_SESSION may reach the
///    socket, and only that write may decide the connect. Any other pre-auth send is dropped
///    with an event.
///  - Drainer lifetime: a drainer whose GameState was replaced (logout) sends nothing, clears
///    its queue and exits.
///
/// No external network: an unconnected socket throws NotConnected on Send, the same
/// SocketException a BeginConnect-pending socket throws (measured on Windows; Linux raises
/// ENOTCONN, also SocketError.NotConnected).
/// </summary>
public class WorldConnectPreAuthGuardTests
{
    private static WorldPacket QuestQuery(uint questId)
    {
        var packet = new WorldPacket(Opcode.CMSG_QUERY_QUEST_INFO);
        packet.WriteUInt32(questId);
        return packet;
    }

    private static WorldPacket AuthSession()
    {
        var packet = new WorldPacket(Opcode.CMSG_AUTH_SESSION);
        packet.WriteUInt32(0);
        return packet;
    }

    private static (GlobalSessionData session, WorldSocket socket, GameSessionData state) SessionWith(WorldClient client)
    {
        var state = GameSessionData.CreateForTesting();
        var session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
        session.GameState = state;
        session.WorldClient = client;
        return (session, WorldSocket.CreateForTest(session), state);
    }

    private static void QueueQuestQuery(GameSessionData state, uint questId, double tokens)
    {
        state.InFlightClientQuestInfoQueries.TryAdd(questId, 0);
        state.PendingQuestQueryQueue.Enqueue(questId);
        state.QuestQueryTokens = tokens;
        state.QuestQueryLastRefillMs = Environment.TickCount64;
        state.QuestQueryDrainerRunning = 1;
    }

    // T1. A non-handshake send while the TCP connect is pending must neither reach the socket
    // nor decide the connect. Red on origin/beta: the NotConnected exception was caught and the
    // catch wrote the outcome as failed.
    [Fact]
    public void NonHandshakeSend_WhileConnectPending_IsDroppedAndDoesNotDecideConnect()
    {
        using var pending = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var wc = new WorldClient();
        wc.EnterConnectPendingForTest(pending);

        wc.SendPacketToServer(QuestQuery(9197));

        Assert.Null(wc.ConnectOutcomeForTest);
        Assert.Equal(0, wc.WriteAttemptsForTest);
        Assert.Equal(1, wc.DroppedBeforeAuthForTest);
    }

    // The handshake write itself must still be able to decide the connect: the connect spin has
    // no timeout, so a failed CMSG_AUTH_SESSION write must end it.
    [Fact]
    public void HandshakeSend_WhileConnectPending_StillDecidesConnectOnWriteError()
    {
        using var pending = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var wc = new WorldClient();
        wc.EnterConnectPendingForTest(pending);

        wc.SendPacketToServer(AuthSession());

        Assert.False(wc.ConnectOutcomeForTest);
        Assert.Equal(1, wc.WriteAttemptsForTest);
        Assert.Equal(0, wc.DroppedBeforeAuthForTest);
    }

    // Once the handshake has completed, sends flow and a later write error never touches the
    // decided outcome.
    [Fact]
    public void NonHandshakeSend_AfterAuth_IsWrittenAndLeavesOutcomeAlone()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var wc = new WorldClient();
        wc.EnterConnectPendingForTest(socket);
        wc.MarkAuthenticatedForTest();

        wc.SendPacketToServer(QuestQuery(9197));

        Assert.True(wc.ConnectOutcomeForTest);
        Assert.Equal(1, wc.WriteAttemptsForTest);
        Assert.Equal(0, wc.DroppedBeforeAuthForTest);
    }

    // T3. The field sender itself: a drainer that reaches a WorldClient whose connect is pending
    // must not decide that connect. Red on origin/beta.
    [Fact]
    public async Task Drainer_DuringPendingConnect_DoesNotDecideConnect()
    {
        using var pending = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var wc = new WorldClient();
        wc.EnterConnectPendingForTest(pending);
        var (session, worldSocket, state) = SessionWith(wc);
        QueueQuestQuery(state, 9197, tokens: 20.0);

        var drain = worldSocket.DrainQuestQueryBucketForTest();
        await Task.WhenAny(drain, Task.Delay(300, TestContext.Current.CancellationToken));

        try
        {
            Assert.Null(wc.ConnectOutcomeForTest);
            Assert.Equal(0, wc.WriteAttemptsForTest);
        }
        finally
        {
            session.WorldClient = null;
            state.PendingQuestQueryQueue.Clear();
        }
    }

    // T5. The field shape of every failing switch: logout replaced the session's GameState while
    // the drainer was waiting for a token. The drainer must send nothing (neither a write nor a
    // pre-auth drop), clear its queue and exit. Red on origin/beta.
    [Fact]
    public async Task Drainer_OfReplacedGameState_SendsNothingClearsQueueAndExits()
    {
        using var pending = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var wc = new WorldClient();
        wc.EnterConnectPendingForTest(pending);
        var (session, worldSocket, oldState) = SessionWith(wc);
        QueueQuestQuery(oldState, 9197, tokens: 0.0); // no token: the drainer awaits ~100 ms first

        var drain = worldSocket.DrainQuestQueryBucketForTest();
        session.GameState = GameSessionData.CreateForTesting(); // the logout completes mid-wait

        bool exited = await Task.WhenAny(drain, Task.Delay(600, TestContext.Current.CancellationToken)) == drain;
        try
        {
            Assert.True(exited, "the drainer of a replaced GameState is still running");
            Assert.Null(wc.ConnectOutcomeForTest);
            Assert.Equal(0, wc.WriteAttemptsForTest);
            Assert.Equal(0, wc.DroppedBeforeAuthForTest);
            Assert.True(oldState.PendingQuestQueryQueue.IsEmpty, "the stale backlog was kept");
            Assert.Equal(0, oldState.QuestQueryDrainerRunning);
        }
        finally
        {
            session.WorldClient = null;
            oldState.PendingQuestQueryQueue.Clear();
        }
    }

    // T6. A recreate that lands during the drainer's token wait (the slot is read at send time,
    // not at gate time) must not decide the new client's connect. Same GameState throughout, so
    // only the WorldClient-side guard can hold this one. Red on origin/beta.
    [Fact]
    public async Task RecreateDuringDrainerTokenWait_DoesNotDecideNewConnect()
    {
        using var oldSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var newSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var oldClient = new WorldClient();
        oldClient.EnterConnectPendingForTest(oldSocket);
        oldClient.MarkAuthenticatedForTest();            // the character-select connection
        var newClient = new WorldClient();
        newClient.EnterConnectPendingForTest(newSocket); // BeginConnect issued, not yet connected
        var (session, worldSocket, state) = SessionWith(oldClient);
        QueueQuestQuery(state, 9197, tokens: 0.0);       // no token: the drainer waits ~100 ms

        var drain = worldSocket.DrainQuestQueryBucketForTest();
        await Task.Delay(30, TestContext.Current.CancellationToken); // inside the token wait
        session.WorldClient = newClient;                               // the recreate

        await Task.WhenAny(drain, Task.Delay(400, TestContext.Current.CancellationToken));
        try
        {
            Assert.Null(newClient.ConnectOutcomeForTest);
            Assert.Equal(0, newClient.WriteAttemptsForTest);
        }
        finally
        {
            session.WorldClient = null;
            state.PendingQuestQueryQueue.Clear();
        }
    }
}
