using System;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Api;
using Ru7100.Core.Ui;

namespace Ru7100.Core.App
{
    /// What a UI host offers to the admin-only dev commands: press a remote key and read back the screen.
    public interface IUiDevTools
    {
        /// Presses `key` `repeat` times, `gapMs` apart (under TvShell.RepeatWindowMs counts as a held key), waits up
        /// to `settleMs` for animations and loads to finish, and returns the text snapshot plus paint statistics.
        Task<object> KeyAsync(RemoteKey key, int repeat, int gapMs, int settleMs, CancellationToken token);

        /// Text snapshot of the current screen plus paint statistics. `nodes` adds the laid-out nodes, `overflow`
        /// the text nodes whose drawn text is larger than their box, `reset` clears the statistics afterwards.
        Task<object> FrameAsync(bool nodes, bool overflow, bool reset, CancellationToken token);
    }

    public static class UiDevCommands
    {
        /// Registers `dev.key` and `dev.frame` (admin scope). Hosts without a shell on screen never call this.
        public static void Register(ApiRouter router, IUiDevTools tools)
        {
            if (router == null) throw new ArgumentNullException(nameof(router));
            if (tools == null) throw new ArgumentNullException(nameof(tools));
            router.Register(new ApiCommand("dev.key", new[] { ApiScopes.Admin }, new[] { "key", "repeat", "gapMs", "settleMs" },
                (args, token) =>
                {
                    var name = args.RequireString("key");
                    if (!Enum.TryParse<RemoteKey>(name, true, out var key) || key == RemoteKey.None || !Enum.IsDefined(typeof(RemoteKey), key))
                        throw ApiException.Invalid("key must be a remote key name such as Up, Down, Left, Right, Select, Back, PlayPause");
                    return tools.KeyAsync(key, args.OptionalInt("repeat", 1, 100) ?? 1, args.OptionalInt("gapMs", 0, 5000) ?? 150,
                        args.OptionalInt("settleMs", 0, 30000) ?? 4000, token);
                }));
            router.Register(new ApiCommand("dev.frame", new[] { ApiScopes.Admin }, new[] { "nodes", "overflow", "reset" },
                (args, token) => tools.FrameAsync(args.OptionalBool("nodes") ?? false, args.OptionalBool("overflow") ?? false, args.OptionalBool("reset") ?? false, token)));
        }
    }
}
