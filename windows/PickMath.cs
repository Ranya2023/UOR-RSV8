namespace Remco;

/// <summary>How far the 🎲 picker spins so it always stops exactly on the chosen name.</summary>
internal static class PickMath
{
    public static int Steps(int count, int start, int winner)
    {
        int laps = count <= 3 ? 8 : count < 10 ? 3 : 1;
        int baseSteps = Math.Max(22, laps * count);
        return baseSteps + ((winner - (start + baseSteps) % count) + count) % count;
    }

    /// <summary>The name index shown when the spin ends.</summary>
    public static int Landing(int count, int start, int steps) => (start + steps) % count;
}
