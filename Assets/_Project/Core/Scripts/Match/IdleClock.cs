namespace PoKingHill
{
    /// <summary>
    /// The clock of the demo (attract) mode's 30 s idle wait and of the camera dwells (the demo's showcase shots, the
    /// broadcast camera's faller cut-away): it adds real frame time but skips any frame longer than
    /// <see cref="MaxFrameSeconds"/> (a scene load, a tab in the background, a GC or shader-compile hitch), so a stall
    /// never counts as idle time or as time a shot was on screen.
    /// </summary>
    public struct IdleClock
    {
        /// <summary>Frames longer than this (s) are skipped.</summary>
        public const float MaxFrameSeconds = 0.25f;

        /// <summary>Seconds counted since the last <see cref="Reset"/>.</summary>
        public float Seconds { get; private set; }

        public void Reset() => Seconds = 0f;

        /// <summary>Adds one frame's time (nothing for a long, negative or NaN frame); returns the new total.</summary>
        public float Advance(float frameSeconds)
        {
            Seconds += Step(frameSeconds);
            return Seconds;
        }

        /// <summary>The part of a frame's time a clock may count: the frame itself, or 0 for a long or bad frame.</summary>
        public static float Step(float frameSeconds) =>
            frameSeconds > 0f && frameSeconds <= MaxFrameSeconds ? frameSeconds : 0f;
    }
}
