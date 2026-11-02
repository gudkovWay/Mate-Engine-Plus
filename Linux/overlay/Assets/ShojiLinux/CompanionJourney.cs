using UnityEngine;

namespace MateEngine.Desktop
{
    public enum CompanionMotionPhase { Idle, Notice, Turn, Start, Steps, Stop, Invite, Wait, Settle }

    public sealed class CompanionJourney
    {
        public const float AuthoringHumanScale = 1.060814f;
        public const float AuthoringAdvance = .4f;
        public CompanionMotionPhase Phase { get; private set; }
        public Vector2 Position { get; private set; }
        public float PhaseTime { get; private set; }
        public float Facing { get; private set; }
        public bool Active { get { return Phase != CompanionMotionPhase.Idle; } }
        public bool Walking { get { return Phase == CompanionMotionPhase.Steps; } }
        Vector2 start, destination;
        float cycle, travelDuration;
        bool viewed;

        public void Begin(Vector2 origin, Vector2 endpoint, float walkCycle, float stridePixels)
        {
            start = Position = origin;
            cycle = Mathf.Max(.1f, walkCycle);
            Facing = endpoint.x < origin.x ? -1f : 1f;
            int cycles = stridePixels > 0 ? Mathf.FloorToInt(Mathf.Abs(endpoint.x - origin.x) / stridePixels + .0001f) : 0;
            destination = origin + new Vector2(Facing * cycles * stridePixels, 0);
            travelDuration = cycles * cycle;
            viewed = false;
            Enter(CompanionMotionPhase.Notice);
        }

        public void Viewed()
        {
            viewed = true;
            if (Phase == CompanionMotionPhase.Wait) Enter(CompanionMotionPhase.Settle);
        }

        public void Cancel() { Enter(CompanionMotionPhase.Idle); }

        public void Tick(float delta)
        {
            if (!Active || delta <= 0f) return;
            float remaining = Mathf.Min(delta, .1f);
            for (int transition = 0; transition < 8 && remaining > 0f && Active; transition++)
            {
                float duration = Duration;
                if (float.IsPositiveInfinity(duration)) return;
                float step = Mathf.Min(remaining, Mathf.Max(0f, duration - PhaseTime));
                PhaseTime += step;
                remaining -= step;
                if (Walking)
                {
                    float t = travelDuration > 0 ? Mathf.Clamp01(PhaseTime / travelDuration) : 1f;
                    Position = Vector2.LerpUnclamped(start, destination, t);
                }
                if (PhaseTime + .00001f < duration) return;
                switch (Phase)
                {
                    case CompanionMotionPhase.Notice: Enter(CompanionMotionPhase.Turn); break;
                    case CompanionMotionPhase.Turn: Enter(CompanionMotionPhase.Start); break;
                    case CompanionMotionPhase.Start: Enter(CompanionMotionPhase.Steps); break;
                    case CompanionMotionPhase.Steps: Enter(CompanionMotionPhase.Stop); break;
                    case CompanionMotionPhase.Stop:
                        Position = destination;
                        Enter(viewed ? CompanionMotionPhase.Settle : CompanionMotionPhase.Invite);
                        break;
                    case CompanionMotionPhase.Invite: Enter(viewed ? CompanionMotionPhase.Settle : CompanionMotionPhase.Wait); break;
                    case CompanionMotionPhase.Settle: Enter(CompanionMotionPhase.Idle); break;
                }
            }
        }

        public float Duration
        {
            get
            {
                switch (Phase)
                {
                    case CompanionMotionPhase.Notice: return .4f;
                    case CompanionMotionPhase.Turn: return .5f;
                    case CompanionMotionPhase.Start: return .35f;
                    case CompanionMotionPhase.Steps: return travelDuration;
                    case CompanionMotionPhase.Stop: return .45f;
                    case CompanionMotionPhase.Invite: return 1.2f;
                    case CompanionMotionPhase.Settle: return .6f;
                    default: return float.PositiveInfinity;
                }
            }
        }
        void Enter(CompanionMotionPhase value) { Phase = value; PhaseTime = 0f; }
    }
}
