using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_TAXI_NODE_STATUS_QUERY)]
    [PacketHandler(Opcode.CMSG_TAXI_QUERY_AVAILABLE_NODES)]
    void HandleTaxiNodesQuery(InteractWithNPC interact)
    {
        WorldPacket packet = new WorldPacket(interact.GetUniversalOpcode());
        packet.WriteGuid(interact.CreatureGUID.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_ENABLE_TAXI_NODE)]
    void HandleEnableTaxiNode(InteractWithNPC interact)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_TALK_TO_GOSSIP);
        packet.WriteGuid(interact.CreatureGUID.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_ACTIVATE_TAXI)]
    void HandleActivateTaxi(ActivateTaxi taxi)
    {
        uint fromNode = GetSession().GameState.CurrentTaxiNode;
        bool direct = TaxiPathExist(fromNode, taxi.Node);

        // direct path exist
        if (direct)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_ACTIVATE_TAXI);
            packet.WriteGuid(taxi.FlightMaster.To64());
            packet.WriteUInt32(fromNode);
            packet.WriteUInt32(taxi.Node);
            SendPacketToServer(packet);
            Log.Event("taxi.activate_requested", new
            {
                flight_master = taxi.FlightMaster.ToString(),
                from_node = fromNode,
                to_node = taxi.Node,
                routing = "direct",
                hop_count = 2,
            });
        }
        else // find shortest path
        {
            HashSet<uint> path = GetTaxiPath(fromNode, taxi.Node, GetSession().GameState.UsableTaxiNodes);
            if (path.Count <= 1) // no nodes found
            {
                Log.Event("taxi.activate_requested", new
                {
                    flight_master = taxi.FlightMaster.ToString(),
                    from_node = fromNode,
                    to_node = taxi.Node,
                    routing = "no_path",
                    hop_count = path.Count,
                });
                return;
            }

            WorldPacket packet = new WorldPacket(Opcode.CMSG_ACTIVATE_TAXI_EXPRESS);
            packet.WriteGuid(taxi.FlightMaster.To64());
            packet.WriteUInt32(0);                // total cost, not used
            packet.WriteUInt32((uint)path.Count); // node count
            foreach (uint itr in path)
                packet.WriteUInt32(itr);
            SendPacketToServer(packet);
            Log.Event("taxi.activate_requested", new
            {
                flight_master = taxi.FlightMaster.ToString(),
                from_node = fromNode,
                to_node = taxi.Node,
                routing = "express",
                hop_count = path.Count,
            });
        }
        GetSession().GameState.IsWaitingForTaxiStart = true;
    }

    // JimsProxy (taxi-flight-robustness): vanilla 1.12 has no early-landing concept —
    // the server is committed to the full SplineTimeFull and ignores any opcode trying
    // to cut a flight short. The modern 1.14 client still surfaces the "Stop at next
    // flight path" button regardless and sends this CMSG when clicked. Bundle
    // 20260504-032731 (attempt_id dc1050b5) showed: server kept flying the original
    // 56s spline to completion regardless of the early-landing CMSG.
    //
    // Therefore: don't forward (server can't honor it, no legacy mapping anyway), and
    // don't cancel the dismount Task — cancelling left the player stuck in flight pose
    // at the natural end of the spline because no one fired the control/gravity/unfly/
    // unroot packets. The fix: log it for visibility, and let the dismount Task fire on
    // its original schedule. Player lands cleanly at the original end destination; the
    // button is purely cosmetic on vanilla.
    //
    // True early-landing emulation is a separate (substantial) follow-up — would need
    // to compute the next taxi waypoint from the spline, force-teleport client+server
    // there, and reconcile zone/instance/cost state. Tracked separately if needed.
    [PacketHandler(Opcode.CMSG_TAXI_REQUEST_EARLY_LANDING)]
    void HandleTaxiRequestEarlyLanding(EmptyClientPacket packet)
    {
        Log.Event("taxi.early_landing_requested_ignored", new
        {
            attempt_id = GetSession().GameState.TaxiAttemptId,
            had_pending_dismount = GetSession().GameState.TaxiDismountCts != null,
            reason = "vanilla_server_no_early_landing_support",
        });
    }
    bool TaxiPathExist(uint from, uint to)
    {
        foreach (var itr in GameData.TaxiPaths)
        {
            if (itr.Value.From == from && itr.Value.To == to ||
                itr.Value.From == to && itr.Value.To == from)
                return true;
        }
        return false;
    }
    bool IsTaxiNodeKnown(uint node, List<byte> usableNodes)
    {
        byte field = (byte)((node - 1) / 8);
        uint submask = (uint)1 << (byte)((node - 1) % 8);
        return (usableNodes[field] & submask) == submask;
    }
    HashSet<uint> GetTaxiPath(uint from, uint to, List<byte> usableNodes)
    {
        // shortest path node list
        HashSet<uint> nodes = new HashSet<uint> { from };
        // copy taxi nodes graph and disable unknown nodes
        int[,] graphCopy = new int[GameData.TaxiNodesGraph.GetLength(0), GameData.TaxiNodesGraph.GetLength(1)];
        Buffer.BlockCopy(GameData.TaxiNodesGraph, 0, graphCopy, 0, GameData.TaxiNodesGraph.Length * sizeof(uint));
        for (uint i = 1; i < graphCopy.GetLength(0); i++)
        {
            if (!IsTaxiNodeKnown(i, usableNodes))
            {
                for (uint itr = 0; itr < graphCopy.GetLength(1); itr++)
                    graphCopy[i, itr] = 0;

                for (uint itr = 0; itr < graphCopy.GetLength(0); itr++)
                    graphCopy[itr, i] = 0;
            }
        }
        int minDist = Dijkstra(graphCopy, (int)from, (int)to, graphCopy.GetLength(0), nodes);
        return nodes;
    }
    int MinDistance(int[] dist, bool[] sptSet, int vCnt)
    {
        int min = int.MaxValue, min_index = -1;
        for (int v = 0; v < vCnt; v++)
            if (sptSet[v] == false && dist[v] <= min)
            {
                min = dist[v];
                min_index = v;
            }
        return min_index;
    }
    void SavePath(int[] parent, int j, HashSet<uint> nodes)
    {
        if (parent[j] == -1)
            return;
        SavePath(parent, parent[j], nodes);
        nodes.Add((uint)j);
    }
    // taken from https://www.geeksforgeeks.org/printing-paths-dijkstras-shortest-path-algorithm/
    int Dijkstra(int[,] graph, int src, int dest, int vCnt, HashSet<uint> nodes)
    {
        int[] dist = new int[vCnt];
        int[] parent = new int[vCnt];
        bool[] sptSet = new bool[vCnt];
        for (int i = 0; i < vCnt; i++)
        {
            dist[i] = int.MaxValue;
            sptSet[i] = false;
            parent[i] = -1;
        }
        dist[src] = 0;
        for (int count = 0; count < vCnt - 1; count++)
        {
            int u = MinDistance(dist, sptSet, vCnt);
            sptSet[u] = true;

            for (int v = 0; v < vCnt; v++)
            {
                if (!sptSet[v] && graph[u, v] != 0 &&
                     dist[u] != int.MaxValue && dist[u] + graph[u, v] < dist[v])
                {
                    parent[v] = u;
                    dist[v] = dist[u] + graph[u, v];
                }
            }
        }
        // save shortest path
        SavePath(parent, dest, nodes);
        // return shortest path distance
        return dist[dest];
    }
}
