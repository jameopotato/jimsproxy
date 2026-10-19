using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#417 symptom 2): the mount display field must reach the client every time an update
// carries it, even when the proxy's modern cache already holds the same value (the 0 before a remount,
// MountCacheResetTracker). SetUpdateField drops equal values; ForceUpdateField does not.
public class MountDisplayForcedEmitTests
{
    private const int Field = (int)UnitField.UNIT_FIELD_MOUNTDISPLAYID;

    [Fact]
    public void SetUpdateField_SameValueTwice_SecondIsNotMarked()
    {
        var fields = new UpdateFieldsArray((uint)Field + 1);
        fields.SetUpdateField<int>(Field, 2786);
        fields.m_updateMask.Clear();

        fields.SetUpdateField<int>(Field, 2786);

        Assert.False(fields.m_updateMask.GetBit(Field));
    }

    [Fact]
    public void ForceUpdateField_SameValueTwice_SecondIsMarked()
    {
        var fields = new UpdateFieldsArray((uint)Field + 1);
        fields.ForceUpdateField(Field, 2786);
        fields.m_updateMask.Clear();

        fields.ForceUpdateField(Field, 2786);

        Assert.True(fields.m_updateMask.GetBit(Field));
        Assert.Equal(2786, fields.GetUpdateField<int>(Field));
    }
}
