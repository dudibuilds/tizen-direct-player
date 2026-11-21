using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Ui;
using Ru7100.RemoteUi;
using Xunit;

namespace Ru7100.Tests.RemoteUi
{
    public class UiFrameDiffTests
    {
        private static UiFrame Row(int focused)
        {
            var frame = new UiFrame { ScreenKey = "home" };
            frame.Text("title", 80, 60, 900, 60, "Home", 48, bold: true);
            for (var i = 0; i < 40; i++)
            {
                var card = frame.Box("card" + i, 80 + i * 260, 300, 240, 360, i == focused ? "#FFFFFF" : "#202028").R(12);
                if (i == focused) { card.X -= 10; card.Y -= 10; card.W += 20; card.H += 20; }
                frame.Image("poster" + i, 80 + i * 260, 300, 240, 360, "https://example.test/p" + i + ".jpg");
                frame.Text("label" + i, 80 + i * 260, 670, 240, 40, "Title " + i, 26);
            }
            return frame;
        }

        private static void AssertSame(UiFrame expected, UiFrame actual)
        {
            Assert.Equal(expected.Background, actual.Background);
            Assert.Equal(expected.ScreenKey, actual.ScreenKey);
            Assert.Equal(expected.Nodes.Select(n => n.Key), actual.Nodes.Select(n => n.Key));
            for (var i = 0; i < expected.Nodes.Count; i++)
                Assert.Equal(JObject.FromObject(expected.Nodes[i]).ToString(), JObject.FromObject(actual.Nodes[i]).ToString());
        }

        [Fact]
        public void FullFrameRoundTrips()
        {
            var frame = Row(0);
            var applied = UiFrameDiff.Apply(null, UiFrameDiff.Make(null, frame));
            AssertSame(frame, applied);
        }

        [Fact]
        public void FocusMoveIsSmallAndAppliesExactly()
        {
            var before = Row(3);
            var after = Row(4);
            var tvBefore = UiFrameDiff.Apply(null, UiFrameDiff.Make(null, before));
            var diff = UiFrameDiff.Make(before, after);
            Assert.Null(diff["order"]);
            Assert.Equal(new[] { "card3", "card4" }, ((JObject)diff["set"]).Properties().Select(p => p.Name).OrderBy(n => n));
            Assert.True(diff.ToString(Newtonsoft.Json.Formatting.None).Length < 300, diff.ToString());

            var tvAfter = UiFrameDiff.Apply(tvBefore, diff);
            AssertSame(after, tvAfter);
            // Copy on write: the animator still holds the previous target and compares against it.
            Assert.Equal("#202028", tvBefore.Nodes.First(n => n.Key == "card4").Fill);
            Assert.Same(tvBefore.Nodes.First(n => n.Key == "poster4"), tvAfter.Nodes.First(n => n.Key == "poster4"));
        }

        [Fact]
        public void RemovalsOrderAndFramePropertiesApply()
        {
            var before = Row(0);
            var after = new UiFrame { ScreenKey = "details", Background = null };
            after.Text("label2", 10, 10, 100, 40, "Moved", 30);
            after.Text("title", 80, 60, 900, 60, "Details", 48, bold: true);
            var tv = UiFrameDiff.Apply(UiFrameDiff.Apply(null, UiFrameDiff.Make(null, before)), UiFrameDiff.Make(before, after));
            AssertSame(after, tv);
            Assert.Null(tv.Background);
        }

        [Fact]
        public void ReorderSendsRunsNotTheWholeList()
        {
            var before = Row(0);
            var after = Row(0);
            var raised = after.Nodes.First(n => n.Key == "card5");
            after.Nodes.Remove(raised);
            after.Nodes.Add(raised);                                 // focused card drawn on top
            after.Nodes.Insert(10, new UiNode { Key = "badge", Kind = UiNodeKind.Box, W = 5, H = 5 });
            after.Nodes.RemoveAll(n => n.Key == "label30");
            var diff = UiFrameDiff.Make(before, after);
            Assert.True(((JArray)diff["order"]).Count <= 8, diff["order"].ToString());
            AssertSame(after, UiFrameDiff.Apply(UiFrameDiff.Apply(null, UiFrameDiff.Make(null, before)), diff));
        }

        [Fact]
        public void DuplicateKeysKeepTheFirstLikeTheAnimator()
        {
            var frame = new UiFrame();
            frame.Box("a", 0, 0, 10, 10, "#111111");
            frame.Box("a", 5, 5, 10, 10, "#222222");
            var tv = UiFrameDiff.Apply(null, UiFrameDiff.Make(null, frame));
            Assert.Single(tv.Nodes);
            Assert.Equal("#111111", tv.Nodes[0].Fill);
        }

        [Fact]
        public void UnchangedFrameGivesEmptyDiff()
        {
            Assert.True(UiFrameDiff.IsEmpty(UiFrameDiff.Make(Row(2), Row(2))));
        }

        [Fact]
        public void ChannelRoundTripsMessages()
        {
            var buffer = new MemoryStream();
            var writer = new RemoteUiChannel(buffer);
            writer.Send(new JObject { ["t"] = "key", ["name"] = "Right", ["seq"] = 7 });
            writer.Send(new JObject { ["t"] = "frame", ["diff"] = UiFrameDiff.Make(null, Row(1)) });
            buffer.Position = 0;
            var reader = new RemoteUiChannel(buffer);
            var first = reader.ReceiveAsync(CancellationToken.None).Result;
            Assert.Equal("Right", (string)first["name"]);
            var second = reader.ReceiveAsync(CancellationToken.None).Result;
            AssertSame(Row(1), UiFrameDiff.Apply(null, (JObject)second["diff"]));
            Assert.Null(reader.ReceiveAsync(CancellationToken.None).Result);
        }

        [Fact]
        public void ConfigNeedsHostAndToken()
        {
            Assert.Throws<System.FormatException>(() => RemoteUiConfig.Parse("{\"host\":\"10.0.0.2\",\"token\":\"short\"}"));
            var config = RemoteUiConfig.Parse("{\"host\":\"10.0.0.2\",\"token\":\"0123456789abcdef0123\"}");
            Assert.Equal(RemoteUiProtocol.DefaultPort, config.Port);
            Assert.True(config.TokenMatches("0123456789abcdef0123"));
            Assert.False(config.TokenMatches("0123456789abcdef0124"));
        }
    }
}
