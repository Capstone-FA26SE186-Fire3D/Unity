namespace Fire3D.Simulation
{
    /// <summary>
    /// Tunable constants of the hazard surrogate. This is a game model for training, not CFD; values are chosen
    /// so fire/smoke behave believably at room scale and are reproducible with a fixed seed and tick.
    /// </summary>
    public struct HazardSettings
    {
        public float TickSeconds;
        /// <summary>Explicit integration substeps per tick (keeps fast smoke diffusion stable).</summary>
        public int SubSteps;
        public float AmbientC;
        public float MaxFireHeatC;
        public float IgnitionHeatC;
        /// <summary>Fraction of the remaining intensity gained per second while fuel lasts.</summary>
        public float FireGrowthPerSecond;
        public float FireDecayPerSecond;
        /// <summary>Fuel consumed per second at full intensity (cell fuel starts in 0..1).</summary>
        public float FuelBurnPerSecond;
        /// <summary>Ignition rate per second per unit of burning neighbour intensity.</summary>
        public float SpreadRatePerSecond;
        public float HeatDiffusion;
        public float HeatCoolingPerSecond;
        /// <summary>Rate at which a burning cell's temperature approaches the flame temperature.</summary>
        public float FireHeatRelaxPerSecond;
        public float SmokeYieldPerSecond;
        public float SmokeDiffusion;
        public float SmokeVentPerSecond;
        public float StairRisePerSecond;
        /// <summary>Fraction of diffusion that still passes a closed door.</summary>
        public float DoorLeak;
        public float WindAdvection;

        public static HazardSettings Default => new()
        {
            TickSeconds = 0.1f,
            SubSteps = 2,
            AmbientC = 26f,
            MaxFireHeatC = 650f,
            IgnitionHeatC = 140f,
            FireGrowthPerSecond = 0.12f,
            FireDecayPerSecond = 0.08f,
            FuelBurnPerSecond = 0.004f,
            SpreadRatePerSecond = 0.35f,
            HeatDiffusion = 0.3f,
            HeatCoolingPerSecond = 0.12f,
            FireHeatRelaxPerSecond = 1.2f,
            SmokeYieldPerSecond = 0.6f,
            SmokeDiffusion = 4f,
            SmokeVentPerSecond = 0.003f,
            StairRisePerSecond = 0.6f,
            DoorLeak = 0.04f,
            WindAdvection = 0.6f,
        };
    }

    /// <summary>Weights of the risk-aware A* cost (Docs technology section 7).</summary>
    public struct NavigationWeights
    {
        public float Hazard;
        public float Portal;
        public float Blocked;

        public static NavigationWeights Default => new() { Hazard = 12f, Portal = 0.5f, Blocked = 400f };
    }
}
