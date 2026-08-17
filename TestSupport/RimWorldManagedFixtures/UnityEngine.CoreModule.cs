namespace UnityEngine
{
    public class Object
    {
        public static void DontDestroyOnLoad(Object target)
        {
        }

        public static void Destroy(Object target)
        {
        }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; }
    }

    public class Behaviour : Component
    {
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public class GameObject : Object
    {
        public GameObject(string name)
        {
        }

        public T AddComponent<T>() where T : Component, new() => new T();
    }
}
