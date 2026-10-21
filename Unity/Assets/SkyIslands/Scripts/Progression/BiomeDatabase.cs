using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json;
using SkyIslands.Core;
using UnityEngine;

namespace SkyIslands.Progression
{
    [Serializable]
    public sealed class BiomeDefinition
    {
        public string id;
        public string displayName;
        public int firstIsland;
        public int lastIsland;
        public string skyTint;
        public string[] structures;
        public string[] namePrefixes;
        public string[] nameSuffixes;
    }

    /// <summary>Resolved, unique configuration for one of the 200 islands.</summary>
    public sealed class IslandConfig
    {
        public IslandConfig(int level, string name, BiomeDefinition biome, int indexInBiome)
        {
            Level = level;
            Name = name;
            Biome = biome;
            IndexInBiome = indexInBiome;
        }

        public int Level { get; }
        public string Name { get; }
        public BiomeDefinition Biome { get; }
        public int IndexInBiome { get; }
        public BigInteger TotalCost => EconomyRules.IslandCost(Level);

        public string StructureName(int slot) => Biome.structures[slot];

        /// <summary>Addressables key, e.g. "neon_nebula/s2_t3" or "neon_nebula/s2_damaged".</summary>
        public string StructurePrefabKey(int slot, int tier, bool damaged) =>
            damaged ? $"{Biome.id}/s{slot}_damaged" : $"{Biome.id}/s{slot}_t{tier}";
    }

    /// <summary>
    /// Biome ranges live in a JSON TextAsset (Resources/biomes.json) so designers can edit
    /// them without code changes; each island 1..200 gets a unique generated name.
    /// </summary>
    [CreateAssetMenu(menuName = "Sky Islands/Biome Database", fileName = "BiomeDatabase")]
    public sealed class BiomeDatabase : ScriptableObject
    {
        [SerializeField] TextAsset biomesJson;

        BiomeDefinition[] _biomes;
        IslandConfig[] _islands;

        public IReadOnlyList<BiomeDefinition> Biomes
        {
            get { EnsureLoaded(); return _biomes; }
        }

        public IslandConfig GetIsland(int level)
        {
            EnsureLoaded();
            if (level < 1 || level > EconomyRules.IslandCount)
                throw new ArgumentOutOfRangeException(nameof(level), level, "1..200");
            return _islands[level - 1];
        }

        void OnValidate() => _islands = null;  // re-parse after edits in the inspector

        void EnsureLoaded()
        {
            if (_islands != null) return;
            var json = biomesJson != null ? biomesJson : Resources.Load<TextAsset>("biomes");
            _biomes = JsonConvert.DeserializeObject<BiomeFile>(json.text).biomes;
            var islands = new IslandConfig[EconomyRules.IslandCount];
            int expectedFirst = 1;

            foreach (var b in _biomes)
            {
                int count = b.lastIsland - b.firstIsland + 1;
                if (b.firstIsland != expectedFirst || count <= 0)
                    throw new InvalidOperationException($"Biome '{b.id}' must start at island {expectedFirst}.");
                if (b.structures?.Length != EconomyRules.StructuresPerIsland)
                    throw new InvalidOperationException($"Biome '{b.id}' needs exactly {EconomyRules.StructuresPerIsland} structures.");
                if (b.namePrefixes.Length * b.nameSuffixes.Length < count)
                    throw new InvalidOperationException($"Biome '{b.id}' has too few name parts for {count} unique islands.");

                for (int k = 0; k < count; k++)
                {
                    // (k % P, k / P) is unique for k < P*S, so every island name is unique.
                    string name = b.namePrefixes[k % b.namePrefixes.Length] + " " +
                                  b.nameSuffixes[k / b.namePrefixes.Length];
                    int level = b.firstIsland + k;
                    islands[level - 1] = new IslandConfig(level, name, b, k);
                }
                expectedFirst = b.lastIsland + 1;
            }

            if (expectedFirst != EconomyRules.IslandCount + 1)
                throw new InvalidOperationException($"Biomes cover islands 1..{expectedFirst - 1}; expected 1..200.");
            _islands = islands;
        }

        sealed class BiomeFile
        {
            public BiomeDefinition[] biomes = Array.Empty<BiomeDefinition>();  // set by Newtonsoft
        }
    }
}
