using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// SMSG_QUEST_LOG_FULL translation slice (from the #433 dropped-s2c audit):
/// vanilla and TC-master shapes are both empty-body, so the packet must write
/// exactly zero bytes — any accidental payload would desync the modern stream.
/// </summary>
public class QuestLogFullTranslationTests
{
    [Fact]
    public void QuestLogFull_Layout_EmptyBody()
    {
        var packet = new QuestLogFull();

        byte[] buffer = new byte[1];
        Assert.Equal(0, packet.WriteToSpan(buffer));
        Assert.Equal(0, packet.MaxSize);
    }
}
