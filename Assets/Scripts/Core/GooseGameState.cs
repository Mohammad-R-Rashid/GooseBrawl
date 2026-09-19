namespace GooseBrawl
{
    /// <summary>High level flow of one round of Egg Snatcher.</summary>
    public enum GooseGameState
    {
        Boot,
        ScanEnvironment,
        PlaceNest,
        EggReady,
        EggStolen,
        Chasing,
        Danger,
        JumpAttack,
        GameOver
    }
}
