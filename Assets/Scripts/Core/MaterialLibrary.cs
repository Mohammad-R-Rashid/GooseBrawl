using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Holds the material assets the game uses so the URP shaders are guaranteed to be included in
    /// the build. Every getter has a runtime fallback (Shader.Find) so nothing breaks in the Editor
    /// before Goose Brawl > Setup Project has run.
    /// </summary>
    public class MaterialLibrary : MonoBehaviour
    {
        public static MaterialLibrary Instance { get; private set; }

        public Material gooseMaterial;
        [Tooltip("Optional grey goose variant, used on some rounds for variety.")]
        public Material gooseMaterialGrey;
        public Material placeholderGooseMaterial;
        public Material nestMaterial;
        public Material eggMaterial;
        public Material glowMaterial;
        public Material shadowMaterial;
        public Material shadowCatcherMaterial;
        public Material reticleMaterial;
        public Material planeMaterial;
        public Material mockFloorMaterial;
        public Material mockWallMaterial;
        public Material dustMaterial;
        public Material accentMaterial;
        public Material breadMaterial;
        public Material crumbMaterial;
        [Tooltip("Optional real nest model (any FBX/OBJ under Assets/Art/Nest is picked up by Setup Project).")]
        public GameObject nestModel;

        void Awake()
        {
            Instance = this;
        }

        public Material Nest => nestMaterial != null ? nestMaterial : (nestMaterial = ProceduralAssets.LitMaterial("Nest_Runtime", new Color(0.47f, 0.30f, 0.13f), 0.15f));
        public Material Egg => eggMaterial != null ? eggMaterial : (eggMaterial = ProceduralAssets.LitMaterial("Egg_Runtime", new Color(0.99f, 0.94f, 0.80f), 0.62f));
        public Material Glow => glowMaterial != null ? glowMaterial : (glowMaterial = ProceduralAssets.UnlitTransparent("Glow_Runtime", new Color(1f, 0.85f, 0.25f, 0.8f), ProceduralAssets.RadialTexture()));
        public Material Shadow => shadowMaterial != null ? shadowMaterial : (shadowMaterial = ContactShadowFallback());
        public Material ShadowCatcher => shadowCatcherMaterial != null ? shadowCatcherMaterial : (shadowCatcherMaterial = ShadowCatcherFallback());
        public Material Reticle => reticleMaterial != null ? reticleMaterial : (reticleMaterial = ProceduralAssets.UnlitTransparent("Reticle_Runtime", new Color(1f, 0.965f, 0.87f, 0.85f), ProceduralAssets.SoftRingTexture()));
        public Material Plane => planeMaterial != null ? planeMaterial : (planeMaterial = ProceduralAssets.UnlitTransparent("Plane_Runtime", new Color(1f, 0.965f, 0.87f, 0.3f), ProceduralAssets.DotGridTexture()));
        public Material MockFloor => mockFloorMaterial != null ? mockFloorMaterial : (mockFloorMaterial = ProceduralAssets.LitMaterial("MockFloor_Runtime", new Color(0.6f, 0.6f, 0.62f), 0.1f, default, ProceduralAssets.CheckerTexture()));
        public Material MockWall => mockWallMaterial != null ? mockWallMaterial : (mockWallMaterial = ProceduralAssets.LitMaterial("MockWall_Runtime", new Color(0.55f, 0.6f, 0.7f), 0.2f));
        public Material Dust => dustMaterial != null ? dustMaterial : (dustMaterial = ProceduralAssets.UnlitTransparent("Dust_Runtime", new Color(0.85f, 0.78f, 0.62f, 0.7f), ProceduralAssets.RadialTexture()));
        public Material PlaceholderGoose => placeholderGooseMaterial != null ? placeholderGooseMaterial : (placeholderGooseMaterial = ProceduralAssets.LitMaterial("PlaceholderGoose_Runtime", new Color(0.95f, 0.95f, 0.93f), 0.3f));
        public Material Accent => accentMaterial != null ? accentMaterial : (accentMaterial = ProceduralAssets.LitMaterial("Accent_Runtime", new Color(1f, 0.55f, 0.1f), 0.4f));
        public Material Bread => breadMaterial != null ? breadMaterial : (breadMaterial = ProceduralAssets.LitMaterial("Bread_Runtime", new Color(0.95f, 0.85f, 0.7f), 0.12f, default, ProceduralAssets.CrustTexture()));
        public Material Crumbs => crumbMaterial != null ? crumbMaterial : (crumbMaterial = ProceduralAssets.UnlitTransparent("Crumbs_Runtime", new Color(0.8f, 0.58f, 0.3f, 0.9f), ProceduralAssets.RadialTexture()));

        /// <summary>Goose material for this round: the grey variant every third game (deterministic: the demo run is never a dice roll).</summary>
        public Material PickGooseMaterial(int gamesPlayed = 0)
        {
            if (gooseMaterialGrey != null && gooseMaterial != null && gamesPlayed % 3 == 2) return gooseMaterialGrey;
            return gooseMaterial;
        }

        static Material ContactShadowFallback()
        {
            var shader = Shader.Find("GooseBrawl/ContactShadow");
            if (shader == null) return ProceduralAssets.UnlitTransparent("Shadow_Runtime", new Color(0f, 0f, 0f, 0.3f), ProceduralAssets.RadialTexture());
            var m = new Material(shader) { name = "ContactShadow_Runtime" };
            m.SetTexture("_BaseMap", ProceduralAssets.RadialTexture());
            m.SetFloat("_Strength", 0.28f);
            return m;
        }

        static Material ShadowCatcherFallback()
        {
            var shader = Shader.Find("GooseBrawl/ShadowCatcher");
            if (shader == null) return null;
            return new Material(shader) { name = "ShadowCatcher_Runtime" };
        }
    }
}
