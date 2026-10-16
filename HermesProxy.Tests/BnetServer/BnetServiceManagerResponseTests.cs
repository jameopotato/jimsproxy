using System;
using System.Collections.Generic;
using System.Net;
using Bgs.Protocol.Authentication.V1;
using BNetServer;
using BNetServer.Services;
using Framework.Constants;
using Google.Protobuf;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.BnetServer;

/// <summary>
/// The bnet RPC dispatcher must close every request token the client waits on.
/// Methods whose protocol response type is NoData still get a reply (an empty
/// body with status Ok); only one-way methods (NO_RESPONSE) stay silent.
/// </summary>
public class BnetServiceManagerResponseTests
{
    private sealed class RecordingNetwork : BnetServices.INetwork
    {
        public readonly List<(uint ServiceId, OriginalHash Service, uint MethodId, uint Token, BattlenetRpcErrorCode Status, IMessage? Message)> Sent = new();

        public void SendRpcMessage(uint serviceId, OriginalHash service, uint methodId, uint token, BattlenetRpcErrorCode status, IMessage? message)
        {
            Sent.Add((serviceId, service, methodId, token, status, message));
        }

        public void CloseSocket()
        {
        }

        public IPEndPoint? GetRemoteIpEndPoint()
        {
            return new IPEndPoint(IPAddress.Loopback, 1119);
        }
    }

    private static (BnetServices.ServiceManager Manager, RecordingNetwork Net) CreateLoggedInManager()
    {
        var net = new RecordingNetwork();
        var manager = new BnetServices.ServiceManager("Bnet", net, new GlobalSessionData());
        return (manager, net);
    }

    private static CodedInputStream EmptyRequest()
    {
        return new CodedInputStream(Array.Empty<byte>());
    }

    [Theory]
    [InlineData(1u)] // Subscribe
    [InlineData(2u)] // Unsubscribe
    [InlineData(3u)] // Update
    [InlineData(9u)] // BatchUnsubscribe
    public void Invoke_PresenceNoDataMethod_RepliesOkOnSameToken(uint methodId)
    {
        // Arrange
        var (manager, net) = CreateLoggedInManager();
        const uint token = 0x2A;

        // Act
        manager.Invoke(1, OriginalHash.PresenceService, methodId, token, EmptyRequest());

        // Assert
        var reply = Assert.Single(net.Sent);
        Assert.Equal(OriginalHash.PresenceService, reply.Service);
        Assert.Equal(methodId, reply.MethodId);
        Assert.Equal(token, reply.Token);
        Assert.Equal(BattlenetRpcErrorCode.Ok, reply.Status);
    }

    [Fact]
    public void Invoke_ConnectionKeepAlive_OneWayMethodSendsNothing()
    {
        // Arrange
        var (manager, net) = CreateLoggedInManager();

        // Act
        manager.Invoke(1, OriginalHash.ConnectionService, 5, 0x2B, EmptyRequest());

        // Assert
        Assert.Empty(net.Sent);
    }

    [Fact]
    public void Invoke_VerifyWebCredentials_RepliesOkAfterLogonComplete()
    {
        // Arrange: the handler is Unauthorized-only, so start with no session
        // and seed the ticket store the way the login flow would.
        const string ticket = "presence-ack-test-ticket";
        const uint token = 0x2C;
        var net = new RecordingNetwork();
        var manager = new BnetServices.ServiceManager("Bnet", net, null);
        var pending = new GlobalSessionData { Username = "presence-ack-test" };
        BnetSessionTicketStorage.SessionsByTicket[ticket] = pending;
        try
        {
            var request = new VerifyWebCredentialsRequest { WebCredentials = ByteString.CopyFromUtf8(ticket) };

            // Act
            manager.Invoke(1, OriginalHash.AuthenticationService, 7, token, new CodedInputStream(request.ToByteArray()));

            // Assert: OnLogonComplete first, then the empty Ok reply that closes the token.
            Assert.Equal(2, net.Sent.Count);
            Assert.Equal(OriginalHash.AuthenticationListener, net.Sent[0].Service);
            Assert.Equal(5u, net.Sent[0].MethodId);
            var reply = net.Sent[1];
            Assert.Equal(OriginalHash.AuthenticationService, reply.Service);
            Assert.Equal(7u, reply.MethodId);
            Assert.Equal(token, reply.Token);
            Assert.Equal(BattlenetRpcErrorCode.Ok, reply.Status);
        }
        finally
        {
            BnetSessionTicketStorage.SessionsByTicket.Remove(ticket);
        }
    }
}
