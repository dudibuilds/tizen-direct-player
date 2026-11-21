using System;

namespace Ru7100.Core.Trailers
{
    /// Why a trailer cannot play.
    public enum TrailerFailure { Unplayable, AgeRestricted, BotCheck, NoCompatibleFormat, Network }

    public sealed class TrailerUnavailableException : Exception
    {
        public TrailerUnavailableException(TrailerFailure reason, string message, Exception inner = null) : base(message, inner)
        {
            Reason = reason;
        }

        public TrailerFailure Reason { get; }
    }
}
