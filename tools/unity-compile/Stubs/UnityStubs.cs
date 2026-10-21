// Kept in sync by hand with the Unity 6 API surface the game uses; see tools/unity-compile/run.sh.
// Compile-only stand-ins for the Unity 6 APIs that Sky Islands uses. Member names, signatures,
// default parameters and type relationships mirror UnityEngine 6000.0 so that code which
// compiles here compiles in Unity. Runtime behaviour is only provided where tests need it.
using System;
using System.Threading.Tasks;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; }
        public static bool operator ==(Object x, Object y) => ReferenceEquals(x, y);
        public static bool operator !=(Object x, Object y) => !ReferenceEquals(x, y);
        public static implicit operator bool(Object exists) => !ReferenceEquals(exists, null);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => base.GetHashCode();

        public static T Instantiate<T>(T original) where T : Object => throw new NotSupportedException();
        public static T Instantiate<T>(T original, Transform parent) where T : Object => throw new NotSupportedException();
        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation) where T : Object => throw new NotSupportedException();
        public static void Destroy(Object obj) { }
        public static void Destroy(Object obj, float t) { }
    }

    public class Component : Object
    {
        public GameObject gameObject => throw new NotSupportedException();
        public Transform transform => throw new NotSupportedException();
        public T GetComponent<T>() => throw new NotSupportedException();
    }

    public class Behaviour : Component { public bool enabled { get; set; } }
    public class MonoBehaviour : Behaviour { }
    public class ScriptableObject : Object { }
    public sealed class GameObject : Object { public Transform transform => throw new NotSupportedException(); }
    public class Transform : Component
    {
        public Vector3 position { get; set; }
        public Quaternion rotation { get; set; }
    }

    public class TextAsset : Object { public string text => throw new NotSupportedException(); }

    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Vector3 { public float x, y, z; }
    public struct Quaternion { public float x, y, z, w; }

    public enum KeyCode { None = 0, Return = 13, KeypadEnter = 271 }

    public enum ParticleSystemStopBehavior { StopEmittingAndClear, StopEmitting }

    public sealed class ParticleSystem : Component
    {
        public bool isPlaying => false;
        public MainModule main => default;
        public void Play() { }
        public void Stop(bool withChildren, ParticleSystemStopBehavior stopBehavior) { }

        public struct MainModule
        {
            public float duration { get; set; }
            public MinMaxCurve startLifetime { get; set; }
        }

        public struct MinMaxCurve { public float constantMax { get; set; } }
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
    }

    public static class Debug
    {
        public static void Log(object message) => Console.WriteLine(message);
        public static void LogWarning(object message) => Console.WriteLine("[warning] " + message);
        public static void LogError(object message) => Console.WriteLine("[error] " + message);
    }

    public static class Application
    {
        public static string persistentDataPath => Environment.CurrentDirectory;
        public static string identifier => "com.example.skyislands";
    }

    public static class SystemInfo
    {
        public static string deviceUniqueIdentifier => "test-device";
    }

    public static class Mathf
    {
        public static int RoundToInt(float f) => (int)Math.Round(f);
    }

    public class YieldInstruction { }

    public class AsyncOperation : YieldInstruction
    {
        public bool isDone => false;
        public event Action<AsyncOperation> completed { add { } remove { } }
    }

    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute { }

    public abstract class PropertyAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : PropertyAttribute { public TooltipAttribute(string tooltip) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : PropertyAttribute { public RangeAttribute(float min, float max) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MinAttribute : PropertyAttribute { public MinAttribute(float min) { } }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CreateAssetMenuAttribute : Attribute
    {
        public string menuName { get; set; }
        public string fileName { get; set; }
        public int order { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class RequireComponent : Attribute { public RequireComponent(Type requiredComponent) { } }
}

namespace UnityEngine.Networking
{
    public class UploadHandler : IDisposable { public void Dispose() { } }
    public sealed class UploadHandlerRaw : UploadHandler { public UploadHandlerRaw(byte[] data) { } }
    public class DownloadHandler : IDisposable { public string text => null; public void Dispose() { } }
    public sealed class DownloadHandlerBuffer : DownloadHandler { }

    public sealed class UnityWebRequestAsyncOperation : AsyncOperation
    {
        public UnityWebRequest webRequest => null;
    }

    public class UnityWebRequest : IDisposable
    {
        public enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }

        public UnityWebRequest(string url, string method) { }
        public UploadHandler uploadHandler { get; set; }
        public DownloadHandler downloadHandler { get; set; }
        public int timeout { get; set; }
        public Result result => Result.InProgress;
        public string error => null;
        public long responseCode => 0;
        public void SetRequestHeader(string name, string value) { }
        public UnityWebRequestAsyncOperation SendWebRequest() => throw new NotSupportedException();
        public void Dispose() { }
    }
}

namespace UnityEngine.UIElements
{
    public enum DisplayStyle { Flex, None }
    public enum TrickleDown { NoTrickleDown, TrickleDown }
    public enum PickingMode { Position, Ignore }

    public struct StyleEnum<T> where T : struct, IConvertible
    {
        public T value { get; set; }
        public static implicit operator StyleEnum<T>(T v) => new StyleEnum<T> { value = v };
    }

    public interface IStyle
    {
        StyleEnum<DisplayStyle> display { get; set; }
    }

    public interface IEventHandler { }

    public delegate void EventCallback<in TEventType>(TEventType evt);

    public abstract class EventBase { public IEventHandler target { get; set; } }
    public abstract class EventBase<T> : EventBase where T : EventBase<T>, new() { }
    public abstract class KeyboardEventBase<T> : EventBase<T> where T : KeyboardEventBase<T>, new()
    {
        public KeyCode keyCode { get; protected set; }
    }
    public sealed class KeyDownEvent : KeyboardEventBase<KeyDownEvent> { }
    public abstract class PointerEventBase<T> : EventBase<T> where T : PointerEventBase<T>, new() { }
    public sealed class PointerDownEvent : PointerEventBase<PointerDownEvent> { }

    public abstract class CallbackEventHandler : IEventHandler
    {
        public void RegisterCallback<TEventType>(EventCallback<TEventType> callback, TrickleDown useTrickleDown = TrickleDown.NoTrickleDown)
            where TEventType : EventBase<TEventType>, new() { }
    }

    public interface IVisualElementScheduledItem
    {
        IVisualElementScheduledItem StartingIn(long delayMs);
        IVisualElementScheduledItem Every(long intervalMs);
        void Pause();
        void Resume();
    }

    public interface IVisualElementScheduler
    {
        IVisualElementScheduledItem Execute(Action updateEvent);
    }

    public class VisualElement : Focusable
    {
        public string name { get; set; }
        public IStyle style => null;
        public IVisualElementScheduler schedule => null;
        public PickingMode pickingMode { get; set; }
        public void Add(VisualElement child) { }
        public void Clear() { }
        public void AddToClassList(string className) { }
        public void RemoveFromClassList(string className) { }
        public void EnableInClassList(string className, bool enable) { }
        public void SetEnabled(bool value) { }
    }

    public abstract class Focusable : CallbackEventHandler { }

    public class TextElement : VisualElement { public virtual string text { get; set; } }

    public class Label : TextElement
    {
        public Label() { }
        public Label(string text) { }
    }

    public class Button : TextElement
    {
        public Button() { }
        public Button(Action clickEvent) { }
        public event Action clicked { add { } remove { } }
    }

    public abstract class BaseField<TValueType> : VisualElement
    {
        public virtual TValueType value { get; set; }
        public virtual void SetValueWithoutNotify(TValueType newValue) { }
    }

    public abstract class TextInputBaseField<TValueType> : BaseField<TValueType> { }
    public class TextField : TextInputBaseField<string> { }

    public abstract class AbstractProgressBar : VisualElement
    {
        public virtual float value { get; set; }
        public string title { get; set; }
    }
    public class ProgressBar : AbstractProgressBar { }

    public class Scroller : VisualElement
    {
        public float value { get; set; }
        public float highValue { get; set; }
    }

    public class ScrollView : VisualElement
    {
        public Scroller verticalScroller => null;
        public Vector2 scrollOffset { get; set; }
    }

    public sealed class UIDocument : MonoBehaviour
    {
        public VisualElement rootVisualElement => null;
    }

    public static class UQueryExtensions
    {
        public static T Q<T>(this VisualElement e, string name = null, string className = null) where T : VisualElement => null;
        public static VisualElement Q(this VisualElement e, string name = null, string className = null) => null;
    }
}

namespace UnityEngine.ResourceManagement.AsyncOperations
{
    public enum AsyncOperationStatus { None, Succeeded, Failed }

    public struct AsyncOperationHandle<TObject>
    {
        public Task<TObject> Task => null;
        public AsyncOperationStatus Status => AsyncOperationStatus.None;
        public TObject Result => default;
    }
}

namespace UnityEngine.AddressableAssets
{
    using UnityEngine.ResourceManagement.AsyncOperations;

    public static class Addressables
    {
        public static AsyncOperationHandle<GameObject> InstantiateAsync(object key, Transform parent = null,
                                                                        bool instantiateInWorldSpace = false, bool trackHandle = true) => default;
        public static void Release<TObject>(AsyncOperationHandle<TObject> handle) { }
        public static bool ReleaseInstance(GameObject instance) => true;
    }
}
