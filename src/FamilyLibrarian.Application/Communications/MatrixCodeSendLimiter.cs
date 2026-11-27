namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Limits how often one account can have the bot send a connection code. Every send invites whatever Matrix ID was
/// typed, so without a limit a signed-in member could flood someone else with invitations.
/// </summary>
/// <remarks>
/// Only attempts that reach the homeserver count: a mistyped username refused beforehand costs nothing. The counts
/// are held in memory, which suits the single application host this product runs as, and start again after a restart.
/// </remarks>
public sealed class MatrixCodeSendLimiter
{
    public const int MaxSends = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> sends = [];

    /// <param name="wait">When refused, how long until this account may send again.</param>
    public bool TryRecord(Guid userId, DateTimeOffset now, out TimeSpan wait)
    {
        lock (gate)
        {
            if (!sends.TryGetValue(userId, out var recent))
            {
                recent = new Queue<DateTimeOffset>();
                sends[userId] = recent;
            }

            // Any window of this length, not fixed quarter hours: each send expires on its own.
            while (recent.Count > 0 && now - recent.Peek() >= Window)
            {
                recent.Dequeue();
            }

            if (recent.Count >= MaxSends)
            {
                wait = recent.Peek() + Window - now;
                return false;
            }

            recent.Enqueue(now);
            wait = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>Says plainly that this is a limit, how long it lasts, and what still works.</summary>
    public static string LimitMessage(TimeSpan wait)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return $"You've sent {MaxSends} codes in the last {Window.TotalMinutes:0} minutes, which is the limit. " +
            $"Nothing is broken: you can send another in about {minutes} {(minutes == 1 ? "minute" : "minutes")}. " +
            "If a code has already reached you, you can still reply with the newest one.";
    }
}
