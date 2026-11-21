using System;

namespace Ru7100.Core.Ui
{
    /// The launch intro (docs/design/09-launch.html, docs/viewing-experience.md). Home owns it and draws it over its own
    /// frame, so Home loads underneath and the logo can land on Home's menu wordmark (same node keys, UiWordmark):
    ///   0.0 s  the front chevron alone, centred on the ink page (what the Tizen splash shows)
    ///   0.4 s  beat one: the back chevron slides out from behind it, the front one nudges forward and settles
    ///   0.8 s  a soft amber glow opens behind the mark
    ///   1.0 s  the mark moves left; from 1.3 s "direct" rises in letter by letter (beat two lands on the "t")
    ///   2.6 s  (or once Home is ready, or on any key) hand-off: the logo flies into the menu while the cover fades
    /// While Home isn't ready after 2.6 s, the logo holds and a thin amber line fills under it.
    public sealed class LaunchIntro
    {
        public const long BeatOneMs = 400, NudgeBackMs = 600, GlowMs = 800, WordMs = 1000, LetterDelayMs = 300, LetterStaggerMs = 50;
        public const long MinHandoffMs = 2600, HandoffMs = 700, CoverFadeMs = 600;
        /// Home is drawn under the cover only once the logo is fully in, so its first paint (rows, art) never competes
        /// with the animated beats; its data loads from the start regardless.
        public const long HomeUnderCoverMs = WordMs + LetterDelayMs + 5 * LetterStaggerMs + 520 + 150;   // every beat has finished moving
        /// If the logo isn't drawable this long after launch (first run, slow generation), skip the intro entirely
        /// rather than stall the UI thread.
        public const long GiveUpMs = 6000;
        /// Never hold longer than this after the intro started: hand over even if Home has nothing yet.
        public const long MaxHoldMs = 7000;

        /// The intro lockup is the menu wordmark at this scale (a 132 px mark).
        public const double Scale = 3.3;

        private long startMs = -1;
        private long handoffAtMs = -1;

        public bool Started => startMs >= 0;
        private long createdMs = -1;

        /// Draw Home under the cover yet? (Before the hand-off, only once the logo is fully in.)
        public bool ShowHome(long nowMs) => Done || handoffAtMs >= 0 || (startMs >= 0 && Elapsed(nowMs) >= HomeUnderCoverMs);

        /// Called on every tick until the intro starts: starts it once the logo is drawable (true when it started
        /// now), or gives up after GiveUpMs (Done).
        public bool TryStart(long nowMs, bool logoReady)
        {
            if (startMs >= 0 || Done)
                return false;
            if (createdMs < 0)
                createdMs = nowMs;
            if (logoReady)
            {
                startMs = nowMs;
                return true;
            }
            if (nowMs - createdMs >= GiveUpMs)
                Done = true;
            return false;
        }
        public bool HandingOff => handoffAtMs >= 0;
        public bool Done { get; private set; }

        public void Start(long nowMs)
        {
            if (startMs < 0)
                startMs = nowMs;
        }

        public long Elapsed(long nowMs) => startMs < 0 ? 0 : nowMs - startMs;

        /// Moves the timeline on. `homeReady`: Home has its first row and billboard, so the intro may hand over.
        public void Tick(long nowMs, bool homeReady)
        {
            if (startMs < 0 || Done)
                return;
            if (handoffAtMs < 0 && ((homeReady && Elapsed(nowMs) >= MinHandoffMs) || Elapsed(nowMs) >= MaxHoldMs))
                handoffAtMs = nowMs;
            if (handoffAtMs >= 0 && nowMs - handoffAtMs >= HandoffMs + 60)
                Done = true;
        }

        /// Any key skips straight to the hand-off; keys are swallowed until the intro is done.
        public bool HandleKey(long nowMs)
        {
            if (Done)
                return false;
            if (startMs < 0)
                startMs = nowMs;   // a key before the logo is ready: go straight to the hand-off
            if (handoffAtMs < 0)
                handoffAtMs = nowMs;
            return true;
        }

        public void Draw(UiFrame f, long nowMs)
        {
            var t = Elapsed(nowMs);
            var handoff = handoffAtMs >= 0;

            var cover = f.Box("intro:cover", 0, 0, UiFrame.Width, UiFrame.Height, UiTheme.Background);
            cover.Enter = UiEnter.None;
            // Once Home is drawn underneath, the cover is a hair short of opaque: the TV skips decoding images under a
            // fully opaque layer, so this makes Home's art decode now, while the logo is still, not during the flight.
            cover.Opacity = handoff ? 0 : ShowHome(nowMs) ? 0.996 : 1;
            cover.MoveMs = (int)CoverFadeMs;
            if (startMs < 0)
                return;   // waiting for the logo: the plain ink page, like the launcher's splash

            // Glow: a soft round amber light behind the mark (one radial shadow image).
            const int glowSpread = 440;
            var glow = f.Shadow("intro:glow", UiFrame.Width / 2, UiFrame.Height / 2, 0, 0, 0, glowSpread, 0, UiTheme.Accent);
            glow.Enter = UiEnter.None;
            glow.Opacity = handoff ? 0 : t >= GlowMs ? (t >= WordMs + 400 ? 0.22 : 0.34) : 0;
            glow.MoveMs = handoff ? 300 : t >= WordMs + 400 ? 550 : 500;   // settles by ~2 s, before Home's first paint

            // The lockup, centred as a whole; until the word arrives the mark sits centred on its own.
            var w = UiWordmark.Width(Scale);
            var x0 = (UiFrame.Width - w) / 2;
            var y0 = UiFrame.Height / 2 - UiWordmark.MarkSize * Scale / 2;
            var solo = UiFrame.Width / 2 - UiWordmark.MarkSize * Scale / 2 - x0;
            var menu = UiWordmark.Parts(1, UiWordmark.MenuX, UiWordmark.MenuY);
            var big = UiWordmark.Parts(Scale, x0, y0);
            for (var i = 0; i < big.Count; i++)
            {
                var p = big[i];
                int moveMs;
                if (handoff)
                {
                    p = menu[i];
                    moveMs = (int)HandoffMs;
                }
                else if (!p.IsLetter)
                {
                    var wordIn = t >= WordMs;
                    if (!wordIn)
                        p.X += solo;
                    moveMs = wordIn ? 640 : 420;
                    if (p.Key == UiWordmark.BackKey && t < BeatOneMs)
                    {
                        p.Opacity = 0;
                        p.X -= 46;
                    }
                    if (p.Key == UiWordmark.FrontKey && t >= BeatOneMs && t < NudgeBackMs)
                    {
                        p.X += 10;
                        moveMs = 200;
                    }
                }
                else
                {
                    var shown = t >= WordMs + LetterDelayMs + p.Index * LetterStaggerMs;
                    if (!shown)
                    {
                        p.Opacity = 0;
                        p.Y += 34;
                    }
                    moveMs = 520;
                }
                UiWordmark.Node(f, p).MoveMs = moveMs;
            }

            // Still loading after the intro: a thin amber line fills slowly under the logo.
            if (!handoff && t >= MinHandoffMs + 1400)   // only for a long wait: Home's first paint happens around 2.1-3 s
            {
                const int lw = 300, lh = 4;
                var ly = (int)(y0 + UiWordmark.MarkSize * Scale + 64);
                var lx = (UiFrame.Width - lw) / 2;
                var track = f.Box("intro:load", lx, ly, lw, lh, UiTheme.Text + "1F").R(2);
                track.Enter = UiEnter.Fade;
                var fill = Math.Min(0.92, 0.1 + (t - MinHandoffMs) / 5000.0);
                var bar = f.Box("intro:load:fill", lx, ly, (int)(lw * fill), lh, UiTheme.Accent).R(2);
                bar.Enter = UiEnter.Fade;
                bar.MoveMs = 400;
            }
        }
    }
}
