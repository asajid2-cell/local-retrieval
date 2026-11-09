using MuxTee;

namespace MuxTee.Tests;

// Grid ownership (GRID-OWNERSHIP-CONTRACT.md), tee half: the frames muxd depends on. The pty half -
// that the child's reported console size actually changes - is proved by the contract owner's live
// proof, not here; these pin the wire shapes that make that proof meaningful.
[TestClass]
public class GridOwnershipTests
{
    [TestMethod]
    public void T1_a_local_pane_change_is_announced_as_a_size_frame()
    {
        // TryResize calls SetSize, which sends this shape on every pane change (contract T1). It is a
        // local-ownership statement: muxd sets owner=local from it.
        Assert.AreEqual("{\"t\":\"size\",\"cols\":132,\"rows\":44}", MuxLink.SizeFrame(132, 44));
    }

    [TestMethod]
    public void T2_a_size_ack_echoes_the_epoch_and_the_applied_size()
    {
        string ack = MuxLink.SizeAckFrame(7, true, 60, 30, 209, 51);
        StringAssert.Contains(ack, "\"t\":\"sizeAck\"");
        StringAssert.Contains(ack, "\"epoch\":7");
        StringAssert.Contains(ack, "\"cols\":60");
        StringAssert.Contains(ack, "\"rows\":30");
        StringAssert.Contains(ack, "\"ok\":true");
        // The pane rides along so muxd can publish a committed geometry on a release.
        StringAssert.Contains(ack, "\"paneCols\":209");
        StringAssert.Contains(ack, "\"paneRows\":51");
    }

    [TestMethod]
    public void T2_a_refused_resize_ack_says_so_and_never_claims_a_size()
    {
        string ack = MuxLink.SizeAckFrame(8, false, 0, 0, 209, 51);
        StringAssert.Contains(ack, "\"epoch\":8");
        StringAssert.Contains(ack, "\"ok\":false");
        StringAssert.Contains(ack, "\"paneCols\":209");
    }

    [TestMethod]
    public void activity_is_a_plain_text_frame()
    {
        Assert.AreEqual("{\"t\":\"activity\"}", MuxLink.ActivityFrame());
    }

    [TestMethod]
    public void T2_a_release_is_owner_local_with_no_geometry()
    {
        // Contract section 4: muxd -> tee release is {"t":"resize","epoch":E,"owner":"local"} with no
        // cols/rows; the tee reads the pane itself.
        using var doc = System.Text.Json.JsonDocument.Parse("{\"t\":\"resize\",\"epoch\":5,\"owner\":\"local\"}");
        var root = doc.RootElement;
        Assert.AreEqual("local", MuxLink.ResizeOwner(root));
        Assert.IsTrue(MuxLink.IsRelease("local", 0, 0));
        // A device frame names its geometry and applies.
        Assert.IsFalse(MuxLink.IsRelease("dev-9", 60, 30));
        // Geometry without an owner still applies, so an older sender is never read as a release.
        Assert.IsFalse(MuxLink.IsRelease("", 60, 30));
    }
}
