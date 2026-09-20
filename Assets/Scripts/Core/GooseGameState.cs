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

    /// <summary>What a round looked like, for the results card, the share stamp and the Goose Board.</summary>
    public struct RoundRecap
    {
        public int Dashes, LungesSurvived, Breads, Honks, Dodges;
        public bool Rage, Won, HasShot;
        public float Survival;
        public string GooseName, GooseTitle;
    }
}
