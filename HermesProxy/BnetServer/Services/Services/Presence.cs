// Copyright (c) CypherCore <http://github.com/CypherCore> All rights reserved.
// Licensed under the GNU GENERAL PUBLIC LICENSE. See LICENSE file in the project root for full license information.

using Bgs.Protocol;
using Bgs.Protocol.Presence.V1;
using Framework.Constants;

namespace BNetServer.Services;

// PresenceService stubs. The 1.14.x retail client expects PresenceService to be
// available; without these the client logs "disconnected because no presence"
// and drops the BNet session after some idle threshold (the AFK-DC bug).
//
// We do not actually maintain presence state — these handlers ack with Ok and
// (for query/batch-subscribe) an empty payload. That is enough for the client
// to consider presence "alive" and stop trying to renegotiate.
public partial class BnetServices
{
    [Service(ServiceRequirement.LoggedIn, OriginalHash.PresenceService, 1)]
    BattlenetRpcErrorCode HandlePresenceSubscribe(SubscribeRequest request)
    {
        return BattlenetRpcErrorCode.Ok;
    }

    [Service(ServiceRequirement.LoggedIn, OriginalHash.PresenceService, 2)]
    BattlenetRpcErrorCode HandlePresenceUnsubscribe(UnsubscribeRequest request)
    {
        return BattlenetRpcErrorCode.Ok;
    }

    [Service(ServiceRequirement.LoggedIn, OriginalHash.PresenceService, 3)]
    BattlenetRpcErrorCode HandlePresenceUpdate(UpdateRequest request)
    {
        return BattlenetRpcErrorCode.Ok;
    }

    [Service(ServiceRequirement.LoggedIn, OriginalHash.PresenceService, 4)]
    BattlenetRpcErrorCode HandlePresenceQuery(QueryRequest request, QueryResponse response)
    {
        return BattlenetRpcErrorCode.Ok;
    }

    [Service(ServiceRequirement.LoggedIn, OriginalHash.PresenceService, 8)]
    BattlenetRpcErrorCode HandlePresenceBatchSubscribe(BatchSubscribeRequest request, BatchSubscribeResponse response)
    {
        return BattlenetRpcErrorCode.Ok;
    }

    [Service(ServiceRequirement.LoggedIn, OriginalHash.PresenceService, 9)]
    BattlenetRpcErrorCode HandlePresenceBatchUnsubscribe(BatchUnsubscribeRequest request)
    {
        return BattlenetRpcErrorCode.Ok;
    }
}
