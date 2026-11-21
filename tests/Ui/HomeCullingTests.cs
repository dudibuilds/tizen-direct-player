using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Home lays out only cards that can be seen now or after one key press: a card that appears with an arrow key
    /// must already have been laid out (so it glides in), and rows that cannot scroll sideways leave out the cards
    /// beyond the screen edges (each would cost the TV native objects and a picture load).
    public class HomeCullingTests
    {
        private static readonly Regex CardPicture = new Regex(@"^row:(?<row>.+):\d+:(poster|art)$");

        private static bool OnScreen(UiNode n) => n.X < UiFrame.Width && n.X + n.W > 0 && n.Y < UiFrame.Height && n.Y + n.H > 0;

        private static readonly RemoteKey[] Walk =
        {
            RemoteKey.Down, RemoteKey.Right, RemoteKey.Right, RemoteKey.Right, RemoteKey.Down, RemoteKey.Right, RemoteKey.Right,
            RemoteKey.Down, RemoteKey.Down, RemoteKey.Up, RemoteKey.Left, RemoteKey.Up, RemoteKey.Up, RemoteKey.Down, RemoteKey.Down,
            RemoteKey.Down, RemoteKey.Right, RemoteKey.Left, RemoteKey.Left, RemoteKey.Up, RemoteKey.Up, RemoteKey.Up, RemoteKey.Up,
        };

        [Fact]
        public void EveryCardOnScreenAfterAnArrowWasLaidOutBeforeIt()
        {
            var h = LayoutGridTests.FullHome();
            h.Shell.Start(new HomeViewModel());
            h.Wait(600);
            foreach (var key in Walk)
            {
                var before = new HashSet<string>(UiLayout.Build(h.Shell).Nodes.Select(n => n.Key));
                h.Shell.HandleKey(key);
                h.Clock.Advance(150);
                var after = UiLayout.Build(h.Shell);
                foreach (var node in after.Nodes.Where(n => n.Kind == UiNodeKind.Image && n.Key != null && CardPicture.IsMatch(n.Key) && OnScreen(n)))
                    Assert.True(before.Contains(node.Key), key + ": " + node.Key + " appeared on screen without being laid out before");
            }
        }

        [Fact]
        public void RowsThatCannotScrollSidewaysLeaveOutOffScreenCards()
        {
            var h = LayoutGridTests.FullHome();
            h.Shell.Start(new HomeViewModel());
            h.Wait(600);
            var checkedRows = 0;
            foreach (var key in Walk)
            {
                h.Press(key);
                var vm = (HomeViewModel)h.Shell.Current;
                var focused = vm.FocusedRow?.Key;
                foreach (var node in UiLayout.Build(h.Shell).Nodes.Where(n => n.Kind == UiNodeKind.Image && n.Key != null))
                {
                    var match = CardPicture.Match(node.Key);
                    if (!match.Success || match.Groups["row"].Value == focused)
                        continue;
                    checkedRows++;
                    Assert.True(node.X < UiFrame.Width && node.X + node.W > 0, node.Key + " is laid out beyond the screen edge");
                }
            }
            Assert.True(checkedRows > 0);
        }
    }
}
