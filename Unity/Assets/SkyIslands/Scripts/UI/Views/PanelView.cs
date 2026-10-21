using System;
using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    /// <summary>A slide-up panel that only renders while it is on screen.</summary>
    public abstract class PanelView<TViewModel> : IDisposable where TViewModel : ViewModelBase
    {
        protected PanelView(VisualElement root, TViewModel vm)
        {
            Root = root;
            Vm = vm;
            Vm.Changed += RenderIfVisible;
        }

        protected VisualElement Root { get; }
        protected TViewModel Vm { get; }
        public bool Visible { get; private set; }

        public virtual void Show()
        {
            Visible = true;
            Render();
        }

        public void Hide() => Visible = false;

        public virtual void Dispose() => Vm.Changed -= RenderIfVisible;

        protected abstract void Render();

        void RenderIfVisible()
        {
            if (Visible) Render();
        }

        protected static Label MakeLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        protected static Button MakeButton(string text, Action onClick, bool enabled, params string[] classes)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn");
            button.AddToClassList("btn--small");
            foreach (var c in classes) button.AddToClassList(c);
            button.SetEnabled(enabled);
            return button;
        }
    }
}
