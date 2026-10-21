using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    /// <summary>
    /// Builds the view models and views over MainScreen.uxml once the game is wired, and owns
    /// navigation, toasts and celebrations.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SkyIslandsUI : MonoBehaviour
    {
        const long ToastMs = 2800;
        const long CelebrationMs = 1800;

        [SerializeField] SkyIslandsBootstrap bootstrap;

        readonly List<IDisposable> _owned = new List<IDisposable>();
        readonly Dictionary<string, (VisualElement container, Action show, Action hide)> _panels =
            new Dictionary<string, (VisualElement, Action, Action)>();
        VisualElement _panelHost, _toast, _celebration;
        Label _toastText, _celebrationText, _raidsBadge, _albumBadge;
        IVisualElementScheduledItem _toastTimer, _celebrationTimer;
        string _openPanel;
        HudViewModel _hud;

        void Start()
        {
            if (bootstrap.IsReady) Build();
            else bootstrap.Ready += Build;
        }

        void OnDestroy()
        {
            if (bootstrap != null) bootstrap.Ready -= Build;
            foreach (var d in _owned) d.Dispose();
            _owned.Clear();
        }

        void Build()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _panelHost = root.Q("panel-host");
            _toast = root.Q("toast");
            _toastText = root.Q<Label>("toast-text");
            _celebration = root.Q("celebration");
            _celebrationText = root.Q<Label>("celebration-text");
            _raidsBadge = root.Q<Label>("raids-badge");
            _albumBadge = root.Q<Label>("album-badge");

            _hud = Own(new HudViewModel(bootstrap.Economy, bootstrap.Islands));
            var island = Own(new IslandViewModel(bootstrap.Islands, bootstrap.Economy));
            var raid = Own(new RaidViewModel(bootstrap.Raids, bootstrap.Economy));
            var album = Own(new AlbumViewModel(bootstrap.Cards, bootstrap.Economy));
            var guild = Own(new GuildViewModel(bootstrap.Guilds, bootstrap.GameServices, bootstrap.Economy, bootstrap.Client.UserId));

            Own(new HudView(root, _hud, () => Open("raid-panel")));
            Register(root, "island-panel", c => new IslandView(c, island));
            Register(root, "raid-panel", c => new RaidView(c, raid));
            Register(root, "album-panel", c => new AlbumView(c, album));
            Register(root, "guild-panel", c => new GuildView(c, guild));

            root.Q<Button>("nav-island").clicked += () => Open("island-panel");
            root.Q<Button>("nav-raids").clicked += () => Open("raid-panel");
            root.Q<Button>("nav-album").clicked += () => Open("album-panel");
            root.Q<Button>("nav-guild").clicked += () => Open("guild-panel");
            _panelHost.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == _panelHost) Close();  // tap the dimmed backdrop to dismiss
            });

            _hud.Changed += UpdateBadges;
            UpdateBadges();
        }

        T Own<T>(T disposable) where T : IDisposable
        {
            _owned.Add(disposable);
            if (disposable is ViewModelBase vm)
            {
                vm.Toast += ShowToast;
                vm.Celebrate += ShowCelebration;
            }
            return disposable;
        }

        void Register<TViewModel>(VisualElement root, string name, Func<VisualElement, PanelView<TViewModel>> create)
            where TViewModel : ViewModelBase
        {
            var container = root.Q(name);
            var view = Own(create(container));
            container.Q<Button>("close").clicked += Close;
            _panels[name] = (container, new Action(view.Show), new Action(view.Hide));
        }

        void Open(string name)
        {
            if (_openPanel != null) Close();
            var panel = _panels[name];
            _openPanel = name;
            _panelHost.RemoveFromClassList("hidden");
            panel.container.RemoveFromClassList("hidden");
            panel.show();
        }

        void Close()
        {
            if (_openPanel == null) return;
            var panel = _panels[_openPanel];
            panel.hide();
            panel.container.AddToClassList("hidden");
            _panelHost.AddToClassList("hidden");
            _openPanel = null;
        }

        void UpdateBadges()
        {
            SetBadge(_raidsBadge, _hud.Attacks + bootstrap.Raids.RevengeTargets.Count);
            SetBadge(_albumBadge, _hud.CardPacks);
        }

        static void SetBadge(Label badge, int count)
        {
            badge.EnableInClassList("hidden", count == 0);
            badge.text = count > 99 ? "99+" : count.ToString();
        }

        void ShowToast(string message)
        {
            _toastText.text = message;
            _toast.RemoveFromClassList("hidden");
            _toastTimer?.Pause();
            _toastTimer = _toast.schedule.Execute(() => _toast.AddToClassList("hidden")).StartingIn(ToastMs);
        }

        void ShowCelebration(string message)
        {
            _celebrationText.text = message;
            _celebration.RemoveFromClassList("hidden");
            _celebration.RemoveFromClassList("celebration--pop");
            _celebration.schedule.Execute(() => _celebration.AddToClassList("celebration--pop")).StartingIn(16);
            _celebrationTimer?.Pause();
            _celebrationTimer = _celebration.schedule.Execute(() => _celebration.AddToClassList("hidden")).StartingIn(CelebrationMs);
        }
    }
}
