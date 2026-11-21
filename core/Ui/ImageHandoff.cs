using System;
using System.Collections.Generic;

namespace Ru7100.Core.Ui
{
    /// Pictures that finished downloading and wait to be handed to the toolkit (the TV painter sets `image.File` on
    /// the main loop and EFL decodes the picture in its next render). Without a budget a burst of completions (a row
    /// scrolling in, the cache answering many hits at once) all land in one frame and stall navigation; this queue
    /// hands over a bounded number of pixels per pump, visible art first, and drops stale requests for free.
    /// Add from any thread; Pump on the main loop only.
    public sealed class ImageHandoff<T> where T : class
    {
        /// Pixels handed over per pump while the viewer navigates: two or three card-sized pictures (a 300x450
        /// poster or 480x270 card art is ~0.13 MP).
        public const long NavigatingPixels = 350_000;
        /// Pixels per pump at rest: one full-screen backdrop (1920x1080, ~2.1 MP) plus a little.
        public const long RestingPixels = 2_300_000;

        private readonly object gate = new object();
        private List<T> items = new List<T>();
        private bool armed;

        /// Queues a finished picture. True when the caller must arrange a pump (the queue was idle); false while
        /// a pump is already pending, which will pick this item up.
        public bool Add(T item)
        {
            lock (gate)
            {
                items.Add(item);
                if (armed)
                    return false;
                armed = true;
                return true;
            }
        }

        /// Items waiting (diagnostics).
        public int Count
        {
            get { lock (gate) return items.Count; }
        }

        /// Hands over waiting pictures: stale ones are dropped without counting against the budget, visible ones
        /// go first (oldest first), and handing over stops once `pixelBudget` pixels have been handed over (at
        /// least one picture always goes, so a single backdrop is never starved). `apply` hands one picture over
        /// and returns its pixel count. Returns true while pictures remain (pump again on the next frame); false
        /// when the queue is empty and idle (the next Add asks for a new pump).
        public bool Pump(Func<T, bool> stale, Func<T, bool> visible, Func<T, long> apply, long pixelBudget, Action<T> dropped = null)
        {
            List<T> batch;
            lock (gate)
            {
                batch = items;
                items = new List<T>();
            }
            var leftover = new List<T>();
            long spent = 0;
            var handed = 0;
            bool more;
            var completed = false;
            try
            {
                // Two passes over the same list: visible pictures, then the rest, each in arrival order.
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var item = batch[i];
                        if (item == null)
                            continue;
                        if (stale(item))
                        {
                            batch[i] = null;
                            dropped?.Invoke(item);
                            continue;
                        }
                        if (pass == 0 && !visible(item))
                            continue;
                        batch[i] = null;
                        if (handed > 0 && spent >= pixelBudget)
                        {
                            leftover.Add(item);
                            continue;
                        }
                        handed++;
                        spent += Math.Max(1, apply(item));
                    }
                }
                completed = true;
            }
            finally
            {
                // A callback that throws loses only its own picture: whatever this pump did not reach stays queued,
                // and the queue goes idle so the next Add arms a new pump.
                foreach (var item in batch)
                    if (item != null)
                        leftover.Add(item);
                lock (gate)
                {
                    leftover.AddRange(items);   // arrived during the pump: after what was already waiting
                    items = leftover;
                    more = items.Count > 0;
                    if (!more || !completed)
                        armed = false;
                }
            }
            return more;
        }
    }
}
