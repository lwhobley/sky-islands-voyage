using System.Threading.Tasks;
using UnityEngine;
#if SKYISLANDS_ADDRESSABLES
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
#endif

namespace SkyIslands.Progression
{
    /// <summary>
    /// Renders the current island in the world: one prefab per structure slot, swapped as tiers
    /// change, with a smoke effect while a structure is damaged and bursts on tier and island
    /// completion. Driven entirely by <see cref="IslandProgressionController"/> events.
    ///
    /// Prefabs are looked up by <see cref="IslandConfig.StructurePrefabKey"/>:
    /// "&lt;biome id&gt;/s&lt;slot&gt;_t&lt;tier&gt;" (tier 0 = scaffold) and "&lt;biome id&gt;/s&lt;slot&gt;_damaged".
    /// With the Addressables package installed they load as Addressables under that key;
    /// otherwise from Resources/Structures/&lt;key&gt;.
    /// </summary>
    public sealed class IslandSceneView : MonoBehaviour
    {
        [SerializeField] IslandProgressionController islands;
        [SerializeField, Tooltip("Where each of the 5 structures stands.")]
        Transform[] slotAnchors = new Transform[Core.EconomyRules.StructuresPerIsland];
        [SerializeField, Tooltip("Looping smoke, instanced per slot and played while damaged.")]
        ParticleSystem smokePrefab;
        [SerializeField] ParticleSystem tierCompletePrefab;
        [SerializeField] ParticleSystem islandCompletePrefab;
        [SerializeField] Transform islandCenter;

        readonly GameObject[] _structures = new GameObject[Core.EconomyRules.StructuresPerIsland];
        readonly ParticleSystem[] _smoke = new ParticleSystem[Core.EconomyRules.StructuresPerIsland];
        readonly string[] _shownKeys = new string[Core.EconomyRules.StructuresPerIsland];
        readonly int[] _loadVersion = new int[Core.EconomyRules.StructuresPerIsland];

        void OnEnable()
        {
            islands.IslandLoaded += OnIslandLoaded;
            islands.StructureChanged += OnStructureChanged;
            islands.StructureTierCompleted += OnTierCompleted;
            islands.IslandCompleted += OnIslandCompleted;
        }

        void OnDisable()
        {
            islands.IslandLoaded -= OnIslandLoaded;
            islands.StructureChanged -= OnStructureChanged;
            islands.StructureTierCompleted -= OnTierCompleted;
            islands.IslandCompleted -= OnIslandCompleted;
        }

        void OnDestroy()
        {
            for (int slot = 0; slot < _structures.Length; slot++) ReleaseStructure(slot);
        }

        void OnIslandLoaded(IslandConfig _)
        {
            // New island: clear the old one; StructureChanged follows for every slot.
            for (int slot = 0; slot < _structures.Length; slot++)
            {
                ReleaseStructure(slot);
                SetSmoke(slot, false);
            }
        }

        async void OnStructureChanged(StructureView view)
        {
            int slot = view.Slot;
            SetSmoke(slot, view.State == StructureVisualState.Damaged);
            int version = ++_loadVersion[slot];  // invalidate any in-flight load first
            if (_shownKeys[slot] == view.PrefabKey) return;

            GameObject instance = await LoadAsync(view.PrefabKey, slotAnchors[slot]);
            if (version != _loadVersion[slot] || this == null)
            {
                Release(instance);  // a newer change for this slot won the race
                return;
            }
            ReleaseStructure(slot);
            _structures[slot] = instance;
            _shownKeys[slot] = view.PrefabKey;
        }

        void OnTierCompleted(int slot, int tier) => Burst(tierCompletePrefab, slotAnchors[slot]);

        void OnIslandCompleted(IslandConfig _) => Burst(islandCompletePrefab, islandCenter != null ? islandCenter : transform);

        void SetSmoke(int slot, bool on)
        {
            if (smokePrefab == null) return;
            if (_smoke[slot] == null)
            {
                if (!on) return;
                _smoke[slot] = Instantiate(smokePrefab, slotAnchors[slot]);
            }
            if (on && !_smoke[slot].isPlaying) _smoke[slot].Play();
            else if (!on) _smoke[slot].Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        static void Burst(ParticleSystem prefab, Transform at)
        {
            if (prefab == null || at == null) return;
            var fx = Instantiate(prefab, at.position, at.rotation);
            fx.Play();
            var main = fx.main;
            Destroy(fx.gameObject, main.duration + main.startLifetime.constantMax);
        }

        void ReleaseStructure(int slot)
        {
            Release(_structures[slot]);
            _structures[slot] = null;
            _shownKeys[slot] = null;
        }

#if SKYISLANDS_ADDRESSABLES
        static async Task<GameObject> LoadAsync(string key, Transform parent)
        {
            var handle = Addressables.InstantiateAsync(key, parent);
            await handle.Task;
            if (handle.Status == AsyncOperationStatus.Succeeded) return handle.Result;
            Debug.LogWarning($"[Island] No structure prefab at Addressables key '{key}'");
            Addressables.Release(handle);
            return null;
        }

        static void Release(GameObject instance)
        {
            if (instance != null) Addressables.ReleaseInstance(instance);
        }
#else
        static Task<GameObject> LoadAsync(string key, Transform parent)
        {
            var prefab = Resources.Load<GameObject>("Structures/" + key);
            if (prefab == null) Debug.LogWarning($"[Island] No structure prefab at Resources/Structures/{key}");
            return Task.FromResult(prefab != null ? Instantiate(prefab, parent) : null);
        }

        static void Release(GameObject instance)
        {
            if (instance != null) Destroy(instance);
        }
#endif
    }
}
